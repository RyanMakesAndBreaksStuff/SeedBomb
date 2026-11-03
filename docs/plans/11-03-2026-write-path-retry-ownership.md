# Write-path retry ownership Implementation Plan

Source: `sb-investigate.md` section 5 (recommendations 1 to 9). Branch: `fix/dop-cap-at-server-hint`.

**Goal:** A throttled, slow or faulting Dataverse write fails or pauses once, visibly and cancellably, instead of stalling for minutes inside nested retries and flooding the run sheet with repeated errors.

**Architecture:** `ThrottlePolicy` becomes the only retry owner: the `ServiceClient` is built with `MaxRetryCount = 0`, and the policy honours `Retry-After` on service-protection faults through one run-wide pause gate that every in-flight batch waits on. `BulkCreator` stops amplifying faults (no `ExecuteMultiple` fallback except for the unsupported-message code, no blind resend of a timed-out create) and sizes `CreateMultiple` batches adaptively. The run sheet shows the pause, labels the lookup-preparation phase, and collapses repeated activity lines. Settings and the run snapshot expose the SDK knobs.

**Tech stack:** .NET 10, C# 14, `Microsoft.PowerPlatform.Dataverse.Client` 1.2.27, xunit.v3 + Moq, WPF with CommunityToolkit.Mvvm 8.4.2 and WPF-UI 4.3.

**Scope:** Recommendations 1 to 8 of section 5, and recommendation 9 as a documented checklist. Recommendation 8 (retry budget test) is the pre-change signal of Task 1, not a separate task.

**Out of scope:** The alternative in recommendation 1 (SDK as owner with `MaxRetryCount` 2 or 3) is not planned; the investigation's own opinion (section 4) and the original import both put ownership in the app. The reconcile-count variant of recommendation 3 is not planned: it needs `ReconcileAfterRun`, which is off by default; Task 5 uses the split-or-report variant. Finding F7 (`RunSessionGate` invariant test) is section 3, not section 5. Any change to `DeferredLookupBackfill` beyond what `ThrottlePolicy` already gives it.

**Decisions made from the evidence, flagged for the author:**

- D1. Retry owner is the app (`ThrottlePolicy`); the SDK client gets `MaxRetryCount = 0`.
- D2. A timed-out `CreateMultiple` is split and resent only when the table has an alternate key (`ScopedAlternateKey` makes a duplicate resend fail as a duplicate key instead of writing twice). A table without a key reports the batch as "outcome unknown" (new `BatchError.OutcomeUnknown`), never retryable from the sheet.
- D3. The throttle gate is run-wide, inside `ThrottlePolicy`, rather than per table. One policy instance serves a run (`GenerationPipeline` builds it), and a `Retry-After` is a per-user server state, so a table-scoped pause would let the next table hit the same wall.
- D4. The `Retry-After` wait is honoured as given (no cap). `Task.Delay` takes the run token, so Cancel ends it.
- D5. Adaptive batch size starts at `min(configured, 250)`, grows by 50 % after a call under 20 s, and halves (floor 25) on a client timeout. These are the numbers in recommendation 4.

**Verification note:** this plan was written in a container with no .NET SDK (the install hosts are blocked by the network policy), so none of the `dotnet` commands below were run here. Every expected output is derived from the test source and the code paths read, not observed. Run Step 2 of each task before changing anything and correct an expectation that does not match before continuing.

**Commands used throughout** (run from the repository root; `--filter` narrows to the test named in the step):

```bash
dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~<TestName>"
dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~<TestName>"
dotnet build SeedBomb.slnx
```

`TreatWarningsAsErrors` is on and public members need XML docs (`GenerateDocumentationFile`), so every new public member below carries a `/// <summary>`.

**Task graph:**

| Task | Depends on | Can run in parallel with |
| --- | --- | --- |
| 1 Retry owner | — | 4, 7, 8 |
| 2 Pause gate | 1 | 4, 7, 8 |
| 3 Show the wait | 2 | 4, 7, 8 |
| 4 Fallback only on 0x80040800 | — | 1, 2, 3, 7, 8 |
| 5 No blind resend on timeout | 1, 4 | 7, 8 |
| 6 Adaptive batch size | 5 | 7, 8 |
| 7 Trim pre-run reads | — | 1 to 6, 8 |
| 8 Collapse repeats | — | 1 to 7 |
| 9 Expose the knobs | 1 | 7, 8 |
| 10 Process checklist | — | all |

Tasks 1, 4, 7 and 8 own disjoint files and can be dispatched to separate agents at once. Tasks 2, 3, 5 and 6 all edit `BulkCreator.cs` and `ThrottlePolicy.cs`, so run them in order on one branch.

---

### Task 1: One retry owner, with `Retry-After` honoured by `ThrottlePolicy`

**Owns:** Which layer retries, and the retry classification table (`RetryReason`). The SDK client's `MaxRetryCount` and `RetryPauseTime` values (Task 9 only moves them to settings).

**Files:**

- Modify: `src/SeedBomb.Bulk/ThrottlePolicy.cs` — retry service-protection faults with `Retry-After`, and 502/503 protocol faults with backoff.
- Modify: `src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs:101-110` — build the client with SDK retries off.
- Modify: `src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs` — invert the two tests that assert the old ownership; add the budget test.
- Modify: `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs:581-616` — `CreateAsync_WholeBatchFault_KeepsTheDataverseFaultCode` now expects `IsTransient` true.
- Signal: `ThrottlePolicyTests.ExecuteAsync_ThrottleFaultWithRetryAfter_WaitsOnceAndStaysWithinBudget` — total attempts bounded by `maxRetries + 1`, and the wait is the server's, not exponential.

- [ ] **Step 1: Establish the observable pre-change signal**

Add to `src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs` (inside the class, after `ExecuteAsync_ThrottleFault_IsNotRetriedByPolicy`):

```csharp
    private static FaultException<OrganizationServiceFault> ThrottleFault(int code, TimeSpan retryAfter)
    {
        var fault = new OrganizationServiceFault { ErrorCode = code, Message = "Service protection limit." };
        fault.ErrorDetails["Retry-After"] = retryAfter;
        return new FaultException<OrganizationServiceFault>(fault, "Throttled");
    }

    [Theory]
    [InlineData(-2147015902)]
    [InlineData(-2147015903)]
    [InlineData(-2147015898)]
    public async Task ExecuteAsync_ThrottleFaultWithRetryAfter_WaitsOnceAndStaysWithinBudget(int code)
    {
        // Recommendation 1 and 8: the app is the only retry owner, it waits exactly the server's
        // Retry-After, and the attempt count is bounded by maxRetries + 1 whatever the server says.
        var callCount = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw ThrottleFault(code, TimeSpan.FromMilliseconds(300));
                },
                "account",
                maxRetries: 2,
                CancellationToken.None));

        Assert.Equal(3, callCount);
        // Two waits of 300 ms, no exponential 2 s base: well under 2 s in total.
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(2));
        Assert.True(ThrottlePolicy.IsTransient(ex.InnerException));
    }

    [Fact]
    public async Task ExecuteAsync_ThrottleFaultWithoutRetryAfter_UsesBackoff()
    {
        var callCount = 0;
        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" });
                return Task.FromResult("success");
            },
            "account",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_ThrottleWait_IsCancellable()
    {
        using var cts = new CancellationTokenSource();
        var task = _policy.ExecuteAsync<int>(
            () =>
            {
                cts.CancelAfter(100);
                throw ThrottleFault(-2147015902, TimeSpan.FromMinutes(5));
            },
            "account",
            maxRetries: 1,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
```

Then change the two existing tests that encode the old ownership:

```csharp
    // was: ExecuteAsync_ThrottleFault_IsNotRetriedByPolicy, asserting callCount == 1
    [Fact]
    public async Task ExecuteAsync_ThrottleFault_IsRetriedByPolicy()
    {
        // The SDK client is built with MaxRetryCount = 0, so a service-protection fault reaches
        // the policy on the first attempt and the policy owns the resend.
        var throttleFault = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = -2147015902, Message = "Too many requests" },
            "Throttled");

        var callCount = 0;
        await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw throttleFault;
                },
                "account",
                maxRetries: 1,
                CancellationToken.None));

        Assert.Equal(2, callCount);
    }

    // was: ExecuteAsync_NonThrottleProtocolFault_IsNotRetried, asserting callCount == 1 on (502)
    [Theory]
    [InlineData("(502)")]
    [InlineData("(503)")]
    public async Task ExecuteAsync_GatewayProtocolFault_IsRetried(string status)
    {
        var callCount = 0;
        var result = await _policy.ExecuteAsync(
            () =>
            {
                callCount++;
                if (callCount == 1)
                    throw new ProtocolException($"The remote server returned an unexpected response: {status}  .");
                return Task.FromResult("success");
            },
            "contact",
            maxRetries: 3,
            CancellationToken.None);

        Assert.Equal("success", result);
        Assert.Equal(2, callCount);
    }
```

In `TransientCases`, change the two rows:

```csharp
        { new ProtocolException("The remote server returned an unexpected response: (502)  ."), true },
        { new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147015902 }), true },
```

and keep a non-throttle fault row as false:

```csharp
        { new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147220969 }), false },
```

In `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`, `CreateAsync_WholeBatchFault_KeepsTheDataverseFaultCode`, change the last line:

```csharp
        Assert.True(error.IsTransient);   // was Assert.False: a throttle fault is now the policy's to retry
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ThrottlePolicyTests"`

Expected: `ExecuteAsync_ThrottleFaultWithRetryAfter_WaitsOnceAndStaysWithinBudget` fails three times with `Assert.Equal() Failure: Expected: 3, Actual: 1` (the fault is terminal today). `ExecuteAsync_ThrottleFaultWithoutRetryAfter_UsesBackoff`, `ExecuteAsync_ThrottleWait_IsCancellable`, `ExecuteAsync_ThrottleFault_IsRetriedByPolicy` and `ExecuteAsync_GatewayProtocolFault_IsRetried` fail; `IsTransient_SeparatesNetworkFaultsFromDataFaults` fails for the two changed rows.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Bulk/ThrottlePolicy.cs`. Replace the class summary and the body of `ExecuteAsync`, `RetryReason`, `IsRateLimited`; add `RetryAfterOf` and the fault-code set. Unchanged members (`BaseDelay`, constructor, `IsTransient`, `IsWrappedNetworkFault`, `ComputeDelay`) are not restated.

```csharp
/// <summary>
/// The one retry owner for Dataverse calls. The <c>ServiceClient</c> is built with
/// <c>MaxRetryCount = 0</c>, so every fault reaches this policy on the first attempt. A
/// service-protection fault waits the server's <c>Retry-After</c>; timeouts, network faults,
/// HTTP 429/502/503 and busy endpoints wait an exponential backoff with jitter. Any other fault
/// is terminal.
/// </summary>
public class ThrottlePolicy
{
    // Dataverse service-protection codes: number of requests, execution time, concurrency.
    private static readonly int[] ServiceProtectionCodes = [-2147015902, -2147015903, -2147015898];
```

Inside `ExecuteAsync`, replace the three `catch` blocks after the cancellation one:

```csharp
            // On the last attempt a transient fault falls through to DataGenerationException.
            catch (Exception ex) when (attempt < maxRetries && RetryReason(ex) is { } reason)
            {
                var delay = RetryAfterOf(ex) ?? ComputeDelay(attempt);
                _logger.LogWarning(
                    "{Reason} for {Entity} on attempt {Attempt} of {MaxAttempts} after {CallMs:F0}ms ({ExceptionType}: {ExceptionMessage}). Retrying in {Delay:F0}ms.",
                    reason, entityName, attempt + 1, maxRetries + 1,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ex.GetType().Name, ex.Message, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (FaultException<OrganizationServiceFault> ex)
            {
                throw new DataGenerationException(
                    $"Batch creation failed for '{entityName}' on attempt {attempt + 1}: {ex.Detail?.Message ?? ex.Message}",
                    ex);
            }
            catch (Exception ex)
            {
                throw new DataGenerationException(
                    $"Batch creation failed for '{entityName}' on attempt {attempt + 1}: {ex.Message}",
                    ex);
            }
```

Replace `RetryReason` and `IsRateLimited`:

```csharp
    // The one transient classification: ExecuteAsync retries exactly what IsTransient reports.
    private static string? RetryReason(Exception? ex) => ex switch
    {
        // IN-004: a cancel the caller did not request is a transport timeout (TaskCanceledException
        // from HttpClient); ExecuteAsync rethrows a caller cancel before asking.
        TimeoutException or OperationCanceledException => "Timeout",
        HttpRequestException or IOException => "Network fault",
        EndpointNotFoundException => "Endpoint not found",
        ServerTooBusyException => "Server too busy",
        // Recommendation 1: the SDK no longer retries these; the policy does, honouring Retry-After.
        FaultException<OrganizationServiceFault> f when IsServiceProtection(f) => "Service protection",
        ProtocolException p when IsRateLimited(p) => "HTTP 429",
        ProtocolException p when IsGatewayFault(p) => "HTTP 502/503",
        // ServiceClient wraps socket resets (10054) as CommunicationException over HttpRequestException.
        CommunicationException c when IsWrappedNetworkFault(c) => "Network fault",
        _ => null,
    };

    private static bool IsServiceProtection(FaultException<OrganizationServiceFault> ex) =>
        ex.Detail is { } d && Array.IndexOf(ServiceProtectionCodes, d.ErrorCode) >= 0;

    /// <summary>The server's wait for a service-protection fault, or null when the fault carries none.</summary>
    /// <param name="ex">The failure as thrown by the operation.</param>
    public static TimeSpan? RetryAfterOf(Exception? ex) =>
        ex is FaultException<OrganizationServiceFault> { Detail.ErrorDetails: { } details }
        && details.TryGetValue("Retry-After", out var value) && value is TimeSpan wait && wait > TimeSpan.Zero
            ? wait
            : null;

    // ponytail: matches ServiceClient's own message text. A structured status code never reaches
    // us — ThrowIfResponseIsEmpty formats the code into the message and drops the response.
    private static bool IsRateLimited(ProtocolException ex) =>
        ex.Message.Contains("(429)", StringComparison.Ordinal);

    private static bool IsGatewayFault(ProtocolException ex) =>
        ex.Message.Contains("(502)", StringComparison.Ordinal) || ex.Message.Contains("(503)", StringComparison.Ordinal);
```

`src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs`, the object initializer at lines 106 to 110:

```csharp
            {
                // Recommendation 1: ThrottlePolicy is the only retry owner. It honours Retry-After
                // itself; nested SDK retries (default 10) are what stalled runs for minutes.
                MaxRetryCount = 0,
                RetryPauseTime = TimeSpan.Zero,
                EnableAffinityCookie = false
            }, ct).ConfigureAwait(false);
```

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ThrottlePolicyTests"`

Expected: all tests in the class pass, including the three `InlineData` rows of the budget test.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!` with zero failures. `CreateAsync_WholeBatchFault_KeepsTheDataverseFaultCode` passes with the inverted assertion. `CreateAsync_WholeBatchFails_ReportsEveryLostRow` still passes (429 with `MaxRetries = 0` stays a single attempt).

Run: `dotnet build SeedBomb.slnx`

Expected: `Build succeeded.` with `0 Warning(s)`.

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Bulk/ThrottlePolicy.cs src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs
git commit -m "Make ThrottlePolicy the only retry owner and honour Retry-After"
```

---

### Task 2: One run-wide pause for a `Retry-After`, shared by every in-flight batch

**Owns:** The throttle gate: when a service-protection fault arrives, every batch sharing the policy waits for the same deadline instead of resending independently (recommendation 4, second half).

**Files:**

- Modify: `src/SeedBomb.Bulk/ThrottlePolicy.cs` — a deadline field, `WaitIfPausedAsync` before each attempt, extend the deadline on a service-protection fault.
- Modify: `src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs` — concurrent test.
- Signal: `ThrottlePolicyTests.ExecuteAsync_ConcurrentCallers_ShareOneRetryAfterPause` — a second caller that never faulted waits too.

- [ ] **Step 1: Establish the observable pre-change signal**

```csharp
    [Fact]
    public async Task ExecuteAsync_ConcurrentCallers_ShareOneRetryAfterPause()
    {
        // Recommendation 4: a Retry-After pauses the whole run, not just the batch that was told.
        var policy = new ThrottlePolicy(NullLogger<ThrottlePolicy>.Instance);
        var faulted = new TaskCompletionSource();
        var firstCalls = 0;

        var first = policy.ExecuteAsync(
            () =>
            {
                if (++firstCalls == 1)
                {
                    faulted.SetResult();
                    throw ThrottleFault(-2147015902, TimeSpan.FromMilliseconds(500));
                }
                return Task.FromResult(1);
            },
            "account", maxRetries: 1, CancellationToken.None);

        await faulted.Task;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var second = await policy.ExecuteAsync(() => Task.FromResult(2), "contact", maxRetries: 0, CancellationToken.None);

        Assert.Equal(2, second);
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(350), TimeSpan.FromSeconds(2));
        Assert.Equal(1, await first);
    }
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ExecuteAsync_ConcurrentCallers_ShareOneRetryAfterPause"`

Expected: fails with `Assert.InRange() Failure` (the second call returns in under 350 ms because nothing pauses it).

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Bulk/ThrottlePolicy.cs`. Add a field and a helper; call the helper at the top of each attempt and arm it on a service-protection wait.

```csharp
    // Run-wide pause deadline as a Stopwatch timestamp; 0 means not paused. Shared by every
    // caller of this instance (one per run), so a Retry-After stops the thundering herd.
    private long _pausedUntil;
```

In `ExecuteAsync`, after `ct.ThrowIfCancellationRequested();` at the top of the loop:

```csharp
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
```

In the transient `catch` from Task 1, replace the delay line and the `Task.Delay`:

```csharp
                var retryAfter = RetryAfterOf(ex);
                var delay = retryAfter ?? ComputeDelay(attempt);
                if (retryAfter is { } serverWait)
                    PauseFor(serverWait);
                _logger.LogWarning( /* unchanged */ );
                await Task.Delay(delay, ct).ConfigureAwait(false);
```

Add the helpers:

```csharp
    private void PauseFor(TimeSpan wait)
    {
        var until = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        // Only ever extend: a later, longer Retry-After wins; a shorter one never shortens the pause.
        long seen;
        do
        {
            seen = Interlocked.Read(ref _pausedUntil);
            if (until <= seen) return;
        } while (Interlocked.CompareExchange(ref _pausedUntil, until, seen) != seen);
    }

    private async Task WaitIfPausedAsync(CancellationToken ct)
    {
        var until = Interlocked.Read(ref _pausedUntil);
        if (until == 0) return;
        var remaining = TimeSpan.FromSeconds((double)(until - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ThrottlePolicyTests"`

Expected: all pass, including `ExecuteAsync_ConcurrentCallers_ShareOneRetryAfterPause` and `ExecuteAsync_CalledConcurrently_AllSucceed`.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`, zero failures.

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Bulk/ThrottlePolicy.cs src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs
git commit -m "Pause every in-flight batch for one shared Retry-After"
```

---

### Task 3: The run sheet shows "Server busy, resuming in N s" and Cancel ends the wait

**Owns:** The wait notification path: `ThrottlePolicy.Waiting` event → `BulkCreationProgress` phase `"Waiting"` → `GenerationPipelineProgress` → `ProgressUpdate.Note` → `RunViewModel.StatusHeadline` and activity log.

**Files:**

- Modify: `src/SeedBomb.Bulk/ThrottlePolicy.cs` — raise `Waiting(entity, delay)` before each retry sleep.
- Modify: `src/SeedBomb.Bulk/BulkCreator.cs:91-130` (`CreateAsync`) — subscribe for the run, report a `"Waiting"` snapshot.
- Modify: `src/SeedBomb.Wpf/Services/Generation/ProgressUpdate.cs:14-22` — add `string? Note = null`.
- Modify: `src/SeedBomb.Wpf/Services/Generation/WpfGenerationService.cs:83-91` — pass `p.ErrorMessage` as `Note`.
- Modify: `src/SeedBomb.Wpf/ViewModels/RunViewModel.cs:466-520` (`AcceptProgress`) — handle the `"Waiting"` phase.
- Modify: `src/SeedBomb.Wpf.Tests/RunViewModelTests.cs` — new test; `Update(...)` helper gains the parameter by default value, no change needed.
- Signal: `RunViewModelTests.AcceptProgress_WaitingSnapshot_ShowsTheServerWaitWithoutTouchingCounts`.

- [ ] **Step 1: Establish the observable pre-change signal**

Add to `src/SeedBomb.Wpf.Tests/RunViewModelTests.cs` next to `AcceptProgress_PhaseOnlySnapshot_LogsNoZeroOfZeroCounts`:

```csharp
    [Fact]
    public void AcceptProgress_WaitingSnapshot_ShowsTheServerWaitWithoutTouchingCounts()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);
        vm.AcceptProgress(Update("account", created: 40, total: 100), ["account"], 100);

        vm.AcceptProgress(
            new ProgressUpdate("Waiting", "", 0, 0, 0, 0, 0, TimeSpan.Zero, Note: "Server busy, resuming in 47 s"),
            ["account"], 100);

        Assert.Equal("Server busy, resuming in 47 s", vm.StatusHeadline);
        Assert.Equal("40", vm.RowsWrittenLabel);
        Assert.Contains("Server busy, resuming in 47 s", vm.ActivityLines);
    }
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~AcceptProgress_WaitingSnapshot"`

Expected: compile error `CS1739: The best overload for 'ProgressUpdate' does not have a parameter named 'Note'`.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Wpf/Services/Generation/ProgressUpdate.cs`: add the last positional parameter.

```csharp
/// <param name="Note">Free text for a phase-only snapshot, such as the server's wait. Null for record counts.</param>
public sealed record ProgressUpdate(
    string Phase,
    string EntityName,
    int RecordsCreated,
    int TotalRecords,
    int BatchesCompleted,
    int TotalBatches,
    double RecordsPerMinute,
    TimeSpan Elapsed,
    string? Note = null);
```

`src/SeedBomb.Wpf/Services/Generation/WpfGenerationService.cs`, the `progress.Report(new ProgressUpdate(` call: add `now,` → `now, p.ErrorMessage));`.

`src/SeedBomb.Bulk/ThrottlePolicy.cs`: add an event and raise it just before `await Task.Delay(delay, ct)` in the transient catch.

```csharp
    /// <summary>Raised before each retry sleep with the entity and the wait, so a UI can show it.</summary>
    public event Action<string, TimeSpan>? Waiting;
```

```csharp
                Waiting?.Invoke(entityName, delay);
```

`src/SeedBomb.Bulk/BulkCreator.cs`, in `CreateAsync` right after `GenerationLimits.Validate(config);`:

```csharp
        void ReportWait(string entity, TimeSpan delay) => progress?.Report(new BulkCreationProgress
        {
            Phase = "Waiting",
            EntityLogicalName = string.Empty,
            BatchIndex = 0,
            TotalBatches = 0,
            RecordsCreated = 0,
            TotalRecords = 0,
            ErrorMessage = $"Server busy, resuming in {Math.Ceiling(delay.TotalSeconds):F0} s ({entity})",
        });
        _throttlePolicy.Waiting += ReportWait;
```

and in the existing outer `finally` (the one with `preparedRun?.Dispose();`):

```csharp
            _throttlePolicy.Waiting -= ReportWait;
```

`src/SeedBomb.Wpf/ViewModels/RunViewModel.cs`, `AcceptProgress`, before the `"Linking"` branch:

```csharp
        // A wait snapshot carries no counts: show the server's wait as the headline and leave the
        // ring, rows and metrics as the last real snapshot left them. The next batch restores "Generating…".
        if (string.Equals(u.Phase, "Waiting", StringComparison.Ordinal))
        {
            var note = u.Note ?? "Server busy, waiting…";
            if (!string.Equals(StatusHeadline, "Cancelling…", StringComparison.Ordinal))
                StatusHeadline = note;
            AppendActivity(note);
            return;
        }
```

Cancellability needs no new code: `Task.Delay(delay, ct)` in the policy takes the run token that `CancelAsync` cancels.

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~RunViewModelTests"`

Expected: all pass, including the new test.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`, zero failures (the event is a no-op without a subscriber).

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Bulk/ThrottlePolicy.cs src/SeedBomb.Bulk/BulkCreator.cs src/SeedBomb.Wpf/Services/Generation/ProgressUpdate.cs src/SeedBomb.Wpf/Services/Generation/WpfGenerationService.cs src/SeedBomb.Wpf/ViewModels/RunViewModel.cs src/SeedBomb.Wpf.Tests/RunViewModelTests.cs
git commit -m "Show a service-protection wait on the run sheet"
```

---

### Task 4: `ExecuteMultiple` fallback only for the unsupported-message fault

**Owns:** When a `CreateMultiple` failure is replayed as individual creates: only on `0x80040800` (recommendation 2).

**Files:**

- Modify: `src/SeedBomb.Bulk/BulkCreator.cs:481-516` (`SubmitBatchAsync`) — drop the second `catch`.
- Modify: `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs` — new test.
- Signal: `BulkCreatorTests.CreateAsync_DataFaultOnCreateMultiple_RecordsOneBatchErrorAndNeverFallsBack`.

- [ ] **Step 1: Establish the observable pre-change signal**

Add to `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs` after `CreateAsync_WholeBatchFault_KeepsTheDataverseFaultCode`:

```csharp
    [Fact]
    public async Task CreateAsync_DataFaultOnCreateMultiple_RecordsOneBatchErrorAndNeverFallsBack()
    {
        // Recommendation 2: a data fault (plug-in, required field, duplicate key) on a transactional
        // CreateMultiple is terminal for the batch. Replaying it as 10 Creates spends budget on
        // requests known to fail and floods the sheet with identical lines.
        var (sut, serviceMock) = BuildSut();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<CreateMultipleRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault
            {
                ErrorCode = -2147220969,
                Message = "Attribute validation error",
            }));

        var graph = new DependencyGraph();
        graph.AddNode("account");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 },
            BatchSize = 10,
            MaxRetries = 0,
            MaxParallelism = 1,
        };
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        var error = Assert.Single(result.Errors);
        Assert.Equal(-2147220969, error.FaultCode);
        Assert.Equal(10, error.RowCount);
        Assert.Equal(Enumerable.Range(0, 10), error.RowIndexes);
        serviceMock.Verify(s => s.ExecuteAsync(It.IsAny<ExecuteMultipleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~CreateAsync_DataFaultOnCreateMultiple"`

Expected: fails on the `Verify` (`Expected invocation on the mock should never have been performed, but was 1 times`), or earlier on `Assert.Single` because the unmocked `ExecuteMultipleRequest` returns null and the fallback faults.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Bulk/BulkCreator.cs`, `SubmitBatchAsync`: delete this block entirely.

```csharp
            catch (DataGenerationException ex) when (ex.InnerException is FaultException<OrganizationServiceFault>)
            {
                _logger.LogWarning(
                    ex,
                    "CreateMultiple failed for {Entity}; performing single ExecuteMultiple fallback batch.",
                    entityName);
            }
```

The remaining `catch (DataGenerationException ex) when (IsCreateMultipleUnsupported(ex))` still marks the entity and falls through to the `ExecuteMultiple` call. Any other `DataGenerationException` now propagates to the `catch` in `SubmitEntityBatchesAsync`, which already records one `BatchError` with `FaultCode`, `RowCount = batch.Length` and `RowIndexes = rows`. Update the class summary line 26 to: `/// Uses <c>CreateMultiple</c> where available and falls back to <c>ExecuteMultiple</c> only when Dataverse reports the message unsupported.`

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~CreateAsync_DataFaultOnCreateMultiple"`

Expected: passes.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`. `AnswerCreates` still routes every create through `ExecuteMultiple` because it throws `0x80040800`, so the existing fallback tests are unaffected.

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Bulk/BulkCreator.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs
git commit -m "Fall back to ExecuteMultiple only when CreateMultiple is unsupported"
```

---

### Task 5: A timed-out create is split (keyed table) or reported as unknown, never resent whole

**Owns:** The client-timeout outcome for a write (recommendation 3). `BatchError.OutcomeUnknown` and its wording on the sheet.

**Files:**

- Modify: `src/SeedBomb.Core/Contracts/BatchError.cs` — `OutcomeUnknown` property.
- Modify: `src/SeedBomb.Bulk/ThrottlePolicy.cs` — `ExecuteAsync` gains `bool resendOnTimeout = true`.
- Modify: `src/SeedBomb.Bulk/BulkCreator.cs` (`SubmitBatchAsync`, `SubmitEntityBatchesAsync`) — split or report.
- Modify: `src/SeedBomb.Wpf/ViewModels/RunViewModel.cs:911-937` (`BuildGroup`) — hint and disposition.
- Modify: `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`, `src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs` — tests.
- Signal: `BulkCreatorTests.CreateAsync_ClientTimeoutOnUnkeyedTable_ReportsUnknownOutcomeWithoutResending` and `..._OnKeyedTable_SplitsTheBatchOnce`.

- [ ] **Step 1: Establish the observable pre-change signal**

`src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs`:

```csharp
    [Fact]
    public async Task ExecuteAsync_TimeoutWithResendDisabled_IsTerminalAndStillTransient()
    {
        // Recommendation 3: a create that timed out may have committed; the caller decides what to
        // do, the policy must not resend it. IsTransient still reports it, so the sheet can label it.
        var callCount = 0;
        var ex = await Assert.ThrowsAsync<DataGenerationException>(() =>
            _policy.ExecuteAsync<int>(
                () =>
                {
                    callCount++;
                    throw new TaskCanceledException("HttpClient.Timeout elapsed");
                },
                "account", maxRetries: 3, CancellationToken.None, resendOnTimeout: false));

        Assert.Equal(1, callCount);
        Assert.IsType<TaskCanceledException>(ex.InnerException);
    }
```

`src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs` (uses the file's existing `KeyedAccount` helper for the keyed case):

```csharp
    [Fact]
    public async Task CreateAsync_ClientTimeoutOnUnkeyedTable_ReportsUnknownOutcomeWithoutResending()
    {
        var (sut, serviceMock) = BuildSut();
        var sends = 0;
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<CreateMultipleRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((_, _) =>
            {
                sends++;
                throw new TaskCanceledException("HttpClient.Timeout elapsed");
            });

        var graph = new DependencyGraph();
        graph.AddNode("account");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 },
            BatchSize = 10,
            MaxRetries = 3,
            MaxParallelism = 1,
        };
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.Equal(1, sends);
        var error = Assert.Single(result.Errors);
        Assert.True(error.OutcomeUnknown);
        Assert.False(error.IsTransient);
        Assert.Equal(10, error.RowCount);
        Assert.Contains("outcome unknown", error.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ClientTimeoutOnKeyedTable_SplitsTheBatchOnce()
    {
        // A table with an alternate key makes a duplicate resend fail as a duplicate key rather than
        // write twice, so the halves can be resent. Sizes seen: 10 (timeout), then 5 and 5.
        var (sut, serviceMock) = BuildSut();
        var sizes = new List<int>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<CreateMultipleRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                var count = ((CreateMultipleRequest)req).Targets.Entities.Count;
                sizes.Add(count);
                if (count == 10) throw new TaskCanceledException("HttpClient.Timeout elapsed");
                return Task.FromResult<OrganizationResponse>(new CreateMultipleResponse
                {
                    Results = { ["Ids"] = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray() }
                });
            });

        var graph = new DependencyGraph();
        graph.AddNode("account");
        var config = KeyedConfig("scope") with
        {
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 },
            BatchSize = 10,
            MaxRetries = 3,
            MaxParallelism = 1,
        };
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = KeyedAccount(),
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.Equal([10, 5, 5], sizes);
        Assert.Equal(10, result.TotalRecords);
        Assert.Empty(result.Errors);
    }
```

Read `KeyedConfig` and `KeyedAccount` at `BulkCreatorTests.cs:1024-1043` before writing; if `KeyedConfig` already fixes `RecordCounts` or `BatchSize`, the `with` above overrides them.

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ClientTimeout|FullyQualifiedName~TimeoutWithResendDisabled"`

Expected: compile errors `CS1739 ... 'resendOnTimeout'` and `CS1061 'BatchError' does not contain a definition for 'OutcomeUnknown'`.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Core/Contracts/BatchError.cs`, inside the record body after `RowIndexes`:

```csharp
    /// <summary>
    /// The request timed out on the client after it may have reached the server, so these rows may
    /// or may not exist. Never offered for retry; the user checks the table first.
    /// </summary>
    public bool OutcomeUnknown { get; init; }
```

`src/SeedBomb.Bulk/ThrottlePolicy.cs`, `ExecuteAsync` signature and the transient `when`:

```csharp
    /// <param name="resendOnTimeout">
    /// False for a non-idempotent write: a transport timeout is then terminal on the first attempt,
    /// because the server may have committed it. Reads and idempotent updates leave the default.
    /// </param>
    public async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        string entityName,
        int maxRetries,
        CancellationToken ct,
        bool resendOnTimeout = true)
```

```csharp
            catch (Exception ex) when (attempt < maxRetries && RetryReason(ex) is { } reason
                && (resendOnTimeout || ex is not (TimeoutException or OperationCanceledException)))
```

`src/SeedBomb.Bulk/BulkCreator.cs`. `SubmitBatchAsync` gains a `bool hasAlternateKey` parameter and handles the timeout:

```csharp
    private async Task<(List<Guid> ids, List<BatchError> errors)> SubmitBatchAsync(
        string entityName,
        Entity[] batch,
        int[] rows,
        int maxRetries,
        bool hasAlternateKey,
        BatchTrace trace,
        CancellationToken ct)
    {
        if (_messageChecker.ShouldAttemptCreateMultiple(entityName))
        {
            try
            {
                return await _throttlePolicy.ExecuteAsync(
                    () => trace.CallAsync("CreateMultiple", () => CreateMultipleAsync(entityName, batch, ct)),
                    entityName, maxRetries, ct, resendOnTimeout: false).ConfigureAwait(false);
            }
            catch (DataGenerationException ex) when (IsCreateMultipleUnsupported(ex))
            {
                /* unchanged */
            }
            catch (DataGenerationException ex) when (IsClientTimeout(ex, ct))
            {
                return await ResolveTimedOutBatchAsync(entityName, batch, rows, maxRetries, hasAlternateKey, trace, ex, ct)
                    .ConfigureAwait(false);
            }
        }

        return await _throttlePolicy.ExecuteAsync(
            () => trace.CallAsync("ExecuteMultiple", () => ExecuteMultipleFallbackAsync(entityName, batch, rows, ct)),
            entityName, maxRetries, ct, resendOnTimeout: false).ConfigureAwait(false);
    }

    private static bool IsClientTimeout(DataGenerationException ex, CancellationToken ct) =>
        ex.InnerException is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested;

    // Recommendation 3: the server may have committed a batch whose response never arrived. With an
    // alternate key a duplicate resend is rejected as a duplicate, so halves can be resent; without
    // one the rows are reported as unknown and never resent. A batch of one row cannot be split.
    private async Task<(List<Guid> ids, List<BatchError> errors)> ResolveTimedOutBatchAsync(
        string entityName, Entity[] batch, int[] rows, int maxRetries, bool hasAlternateKey,
        BatchTrace trace, DataGenerationException timeout, CancellationToken ct)
    {
        if (hasAlternateKey && batch.Length > 1)
        {
            var half = batch.Length / 2;
            _logger.LogWarning("CreateMultiple for {Entity} timed out after {CallMs:F0}ms; resending as {Left} + {Right} rows (alternate key rejects duplicates).",
                entityName, trace.LastCallMs, half, batch.Length - half);
            var left = await SubmitBatchAsync(entityName, batch[..half], rows[..half], maxRetries, hasAlternateKey, trace, ct).ConfigureAwait(false);
            var right = await SubmitBatchAsync(entityName, batch[half..], rows[half..], maxRetries, hasAlternateKey, trace, ct).ConfigureAwait(false);
            return ([.. left.ids, .. right.ids], [.. left.errors, .. right.errors]);
        }

        _logger.LogWarning("CreateMultiple for {Entity} timed out after {CallMs:F0}ms; {Rows} rows have an unknown outcome and were not resent.",
            entityName, trace.LastCallMs, batch.Length);
        return ([], [new BatchError(entityName,
            $"Timed out after {trace.LastCallMs / 1000:F0} s; outcome unknown. The server may have written these rows: check the table before retrying.",
            null, batch.Length, IsTransient: false)
        {
            RowIndexes = rows,
            OutcomeUnknown = true,
        }]);
    }
```

In `SubmitEntityBatchesAsync`, pass the new argument. The caller `CreateEntityRecordsAsync` already computes `alternateKeyAttrs`; add a parameter `bool hasAlternateKey` to `SubmitEntityBatchesAsync` and pass `alternateKeyAttrs.Length > 0` at the call site (`BulkCreator.cs:383`), then forward it in the `SubmitBatchAsync(` call at line 428.

`src/SeedBomb.Wpf/ViewModels/RunViewModel.cs`, `BuildGroup`. After `var transient = ...`:

```csharp
        var unknown = grouping.Any(e => e.OutcomeUnknown);
        // CR-002: only rows Bulk can locate can be regenerated; link-phase errors carry none.
        var retryable = transient && !unknown && rows.Length > 0;
        var message = grouping.Key.ErrorMessage;
        var hint = unknown
            ? "Timed out after the request may have reached the server — check the table for these rows before retrying."
            : retryable
                ? "Transient throttle or timeout — safe to retry."
                : /* rest unchanged */
```

and in the returned object:

```csharp
            DispositionLabel = unknown ? "Unknown" : retryable ? "Retryable" : "Needs a fix",
            DispositionKey = unknown ? "Unknown" : retryable ? "Retryable" : "FixFirst",
```

`src/SeedBomb.Wpf/Views/Pages/RunSummaryPage.xaml:235` has one `DataTrigger` on `DispositionKey`, for `Retryable` only, so an `Unknown` row renders like `FixFirst` with no XAML change.

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ClientTimeout|FullyQualifiedName~TimeoutWithResendDisabled"`

Expected: the three tests pass; `sizes` equals `[10, 5, 5]`.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`. `ExecuteAsync_TransportTimeoutCancellation_IsRetried` still passes (default `resendOnTimeout: true`). The `[timing] batch` line for a split batch shows `3 attempt(s)` and `CreateMultiple` as the API; no test asserts attempts for that path.

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~RunViewModelTests"`

Expected: all pass.

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Core/Contracts/BatchError.cs src/SeedBomb.Bulk/ThrottlePolicy.cs src/SeedBomb.Bulk/BulkCreator.cs src/SeedBomb.Wpf/ViewModels/RunViewModel.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs src/SeedBomb.Bulk.Tests/ThrottlePolicyTests.cs
git commit -m "Split or report a timed-out create instead of resending it"
```

---

### Task 6: Adaptive `CreateMultiple` batch size

**Owns:** How many rows a create batch carries at any moment (recommendation 4, first half): `AdaptiveBatchSize` in `SeedBomb.Bulk`.

**Files:**

- Create: `src/SeedBomb.Bulk/AdaptiveBatchSize.cs` — the sizing rule.
- Modify: `src/SeedBomb.Bulk/BulkCreator.cs:371-470` — slice rows on demand instead of pre-chunking.
- Modify: `src/SeedBomb.Bulk/RunDiagnostics.cs:36` — snapshot line names the starting batch size.
- Modify: `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs` — new tests.
- Signal: `BulkCreatorTests.CreateAsync_AdaptiveBatchSize_StartsAt250AndGrowsTowardConfigured`.

- [ ] **Step 1: Establish the observable pre-change signal**

```csharp
    [Fact]
    public void AdaptiveBatchSize_StartsLowGrowsOnFastCallsAndHalvesOnTimeout()
    {
        var sizer = new AdaptiveBatchSize(configured: 1000);
        Assert.Equal(250, sizer.Current);

        sizer.ObserveCall(TimeSpan.FromSeconds(5));
        Assert.Equal(375, sizer.Current);
        sizer.ObserveCall(TimeSpan.FromSeconds(25));
        Assert.Equal(375, sizer.Current);
        for (var i = 0; i < 10; i++) sizer.ObserveCall(TimeSpan.FromSeconds(1));
        Assert.Equal(1000, sizer.Current);

        sizer.ObserveTimeout();
        Assert.Equal(500, sizer.Current);
        for (var i = 0; i < 10; i++) sizer.ObserveTimeout();
        Assert.Equal(25, sizer.Current);

        Assert.Equal(10, new AdaptiveBatchSize(configured: 10).Current);
    }

    [Fact]
    public async Task CreateAsync_AdaptiveBatchSize_StartsAt250AndGrowsTowardConfigured()
    {
        var (sut, serviceMock) = BuildSut();
        var sizes = new List<int>();
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<CreateMultipleRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                var count = ((CreateMultipleRequest)req).Targets.Entities.Count;
                lock (sizes) sizes.Add(count);
                return Task.FromResult<OrganizationResponse>(new CreateMultipleResponse
                {
                    Results = { ["Ids"] = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray() }
                });
            });

        var graph = new DependencyGraph();
        graph.AddNode("account");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 1000 },
            BatchSize = 500,
            MaxParallelism = 1,
        };
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        var result = await sut.CreateAsync(config, metadata, graph);

        Assert.Equal(1000, result.TotalRecords);
        Assert.Equal(250, sizes[0]);
        Assert.Equal(375, sizes[1]);
        Assert.Equal(1000, sizes.Sum());
        Assert.All(sizes, s => Assert.InRange(s, 1, 500));
    }
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~AdaptiveBatchSize"`

Expected: compile error `CS0246: The type or namespace name 'AdaptiveBatchSize' could not be found`.

- [ ] **Step 3: Implement the minimum fix**

Create `src/SeedBomb.Bulk/AdaptiveBatchSize.cs`:

```csharp
namespace SeedBomb.Bulk;

/// <summary>
/// Rows per <c>CreateMultiple</c> for one table. Starts low so the first calls finish inside the
/// service-protection execution budget, grows while calls stay fast, and halves on a client
/// timeout. Thread-safe: batches observe from parallel workers.
/// </summary>
/// <param name="configured">The run's configured batch size; the ceiling.</param>
public sealed class AdaptiveBatchSize(int configured)
{
    /// <summary>First batch size when the configured size is larger.</summary>
    public const int InitialSize = 250;

    /// <summary>A call at or under this is "fast": the next batch grows.</summary>
    public static readonly TimeSpan FastCall = TimeSpan.FromSeconds(20);

    private const int Floor = 25;
    private readonly object _lock = new();
    private int _current = Math.Max(1, Math.Min(configured, InitialSize));

    /// <summary>Rows the next batch should carry.</summary>
    public int Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Grows by half after a fast call, up to the configured size. A slow call keeps the size.</summary>
    /// <param name="duration">How long the last call took.</param>
    public void ObserveCall(TimeSpan duration)
    {
        if (duration > FastCall) return;
        lock (_lock)
            _current = Math.Min(configured, _current + _current / 2);
    }

    /// <summary>Halves the size after a client timeout, not below the floor or the configured size.</summary>
    public void ObserveTimeout()
    {
        lock (_lock)
            _current = Math.Max(Math.Min(Floor, configured), _current / 2);
    }
}
```

`src/SeedBomb.Bulk/BulkCreator.cs`, `CreateEntityRecordsAsync`: replace the pre-chunking at lines 371 to 379 so only `rowIndexes` is computed, and pass it to the submitter:

```csharp
        int[] rowIndexes = config.RowIndexes?.GetValueOrDefault(entityName) is { } retryRows
            ? [.. retryRows.Distinct().Order()]
            : [.. Enumerable.Range(0, recordCount)];
        RunDiagnostics.LogPhase(_logger, "generate", generateStart, entityName);

        var submitStart = Stopwatch.GetTimestamp();
        var submitted = await SubmitEntityBatchesAsync(
            entityName, entities, rowIndexes, config, effectiveDop, alternateKeyAttrs.Length > 0, progress, ct).ConfigureAwait(false);
```

Rewrite `SubmitEntityBatchesAsync` to pull slices from a shared cursor (the body's accounting, logging and progress are kept; only the iteration changes):

```csharp
    private async Task<(List<Guid> ids, List<BatchError> errors, Exception? failure)> SubmitEntityBatchesAsync(
        string entityName,
        List<Entity> entities,
        int[] rowIndexes,
        GenerationConfig config,
        int effectiveDop,
        bool hasAlternateKey,
        IProgress<BulkCreationProgress>? progress,
        CancellationToken ct)
    {
        var plannedCount = rowIndexes.Length;
        var allIds = new List<Guid>(plannedCount);
        var allErrors = new List<BatchError>();
        var createdCount = 0;
        var completedBatches = 0;
        var entityStart = DateTimeOffset.UtcNow;
        var sizer = new AdaptiveBatchSize(config.BatchSize);
        var cursor = 0;
        var idBags = new System.Collections.Concurrent.ConcurrentDictionary<int, List<Guid>>();
        var errorBags = new System.Collections.Concurrent.ConcurrentDictionary<int, List<BatchError>>();

        // Next slice of rows, or null when the table is exhausted. Sized at the moment it is taken.
        (int index, int[] rows)? NextSlice()
        {
            lock (sizer)
            {
                if (cursor >= plannedCount) return null;
                var take = Math.Min(sizer.Current, plannedCount - cursor);
                var slice = (completedBatches + idBags.Count, rowIndexes[cursor..(cursor + take)]);
                cursor += take;
                return slice;
            }
        }

        var submit = Parallel.ForEachAsync(
            Enumerable.Range(0, Math.Min(effectiveDop, Math.Max(1, plannedCount))),
            new ParallelOptions { MaxDegreeOfParallelism = effectiveDop, CancellationToken = ct },
            async (_, innerCt) =>
            {
                while (NextSlice() is { } next)
                {
                    var (batchIndex, rows) = next;
                    var batch = rows.Select(row => entities[row]).ToArray();
                    /* the existing per-batch body from "List<Guid> batchIds;" to the progress.Report(...)
                       call, with these substitutions:
                         - SubmitBatchAsync(entityName, batch, rows, config.MaxRetries, hasAlternateKey, trace, innerCt)
                         - idBags[batchIndex] = batchIds; errorBags[batchIndex] = batchErrors;
                         - after LogBatchTiming: if (failureType is null && !batchErrors.Any(e => e.OutcomeUnknown))
                               sizer.ObserveCall(TimeSpan.FromMilliseconds(trace.LastCallMs));
                           else if (batchErrors.Any(e => e.OutcomeUnknown)) sizer.ObserveTimeout();
                         - TotalBatches for the progress snapshot and LogBatchTiming:
                               var done = Interlocked.Increment(ref completedBatches);
                               var total = done + (int)Math.Ceiling((double)Math.Max(0, plannedCount - cursor) / Math.Max(1, sizer.Current));
                           and BatchIndex = done. */
                }
            });

        /* the existing await/catch block is unchanged */

        foreach (var i in idBags.Keys.Order()) allIds.AddRange(idBags[i]);
        foreach (var i in errorBags.Keys.Order()) allErrors.AddRange(errorBags[i]);
        return (allIds, allErrors, failure);
    }
```

Write the elided per-batch body in full when implementing; it is the current lines 403 to 445 with the substitutions listed. Keep the `[timing] batch` format string unchanged so Phase 2's log readers still parse it. A split in Task 5 also calls `sizer.ObserveTimeout()`: have `ResolveTimedOutBatchAsync` accept the sizer and call it before splitting.

`src/SeedBomb.Bulk/RunDiagnostics.cs`, `LogSnapshot`: change `batch {BatchSize}` to `batch {BatchSize} (starting at {StartBatch})` and pass `Math.Min(config.BatchSize, AdaptiveBatchSize.InitialSize)` after `config.BatchSize`.

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~AdaptiveBatchSize"`

Expected: both tests pass; `sizes` starts `250, 375`.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`. `CreateAsync_LogsOneSnapshot_PhaseIntervals_AndOneTimingRecordPerBatch` still sees 3 batch lines (BatchSize 1 → start size 1). The DOP tests still find `, DOP N` in every timing line. The CR-002 keyed tests still write every row exactly once.

Run: `dotnet test tests/SeedBomb.Integration.Tests`

Expected: `Passed!` (XrmMockup accepts any batch size up to 1,000).

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Bulk/AdaptiveBatchSize.cs src/SeedBomb.Bulk/BulkCreator.cs src/SeedBomb.Bulk/RunDiagnostics.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs
git commit -m "Size CreateMultiple batches adaptively from 250 rows"
```

---

### Task 7: Skip environment reads for in-run lookups and label the preparation phase

**Owns:** When `PreparedLookupRun` reads candidates from the environment, and the `"Preparing lookups"` phase (recommendation 5).

**Files:**

- Modify: `src/SeedBomb.Bulk/PreparedLookupRun.cs:93-112` — skip `ReadTargetAsync` for a `PreferInRun` column whose targets are all created earlier in the run.
- Modify: `src/SeedBomb.Bulk/BulkCreator.cs:108-110` — report the phase before `PrepareAsync`.
- Modify: `src/SeedBomb.Wpf/ViewModels/RunViewModel.cs:510-513` — a phase-only snapshot sets the headline to its phase.
- Modify: `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`, `src/SeedBomb.Wpf.Tests/RunViewModelTests.cs` — tests.
- Signal: `BulkCreatorTests.CreateAsync_ImplicitRequiredLookupWithInRunParent_ReadsNothingFromTheEnvironment`.

- [ ] **Step 1: Establish the observable pre-change signal**

`src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`. The file's `AccountContact()` helper (line 801) builds a contact with a required `parentcustomerid` lookup to account and a graph edge; `ExistingAccounts` (line 794) mocks the candidate read. Add:

```csharp
    [Fact]
    public async Task CreateAsync_ImplicitRequiredLookupWithInRunParent_ReadsNothingFromTheEnvironment()
    {
        // Recommendation 5: the implicit rule prefers this run's parents, so the pre-run
        // RetrieveMultiple over the parent table is wasted budget and an unlabelled stall.
        var (sut, serviceMock) = BuildSut();
        AnswerCreates(serviceMock);
        var (metadata, graph) = AccountContact();
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account", "contact"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 2, ["contact"] = 2 },
            BatchSize = 10,
            MaxParallelism = 1,
        };
        var phases = new List<string>();
        var progress = new InlineProgress(p => phases.Add(p.Phase));

        var result = await sut.CreateAsync(config, metadata, graph, progress);

        Assert.Equal(4, result.TotalRecords);
        serviceMock.Verify(s => s.RetrieveMultipleAsync(
            It.Is<QueryBase>(q => q is QueryExpression && ((QueryExpression)q).EntityName == "account"),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("Preparing lookups", phases);
    }
```

`src/SeedBomb.Wpf.Tests/RunViewModelTests.cs`:

```csharp
    [Fact]
    public void AcceptProgress_PhaseOnlySnapshot_UsesThePhaseAsTheHeadline()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 100, tables: ["account"]);

        vm.AcceptProgress(Update("", created: 0, total: 0, phase: "Preparing lookups"), ["account"], 100);

        Assert.Equal("Preparing lookups…", vm.StatusHeadline);
    }
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ImplicitRequiredLookupWithInRunParent"`

Expected: fails on the `Verify` (`was 1 times`) or on the `Assert.Contains("Preparing lookups", phases)`.

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~PhaseOnlySnapshot_UsesThePhase"`

Expected: `Assert.Equal() Failure ... Expected: "Preparing lookups…" Actual: "Generating…"`.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Bulk/PreparedLookupRun.cs`, in `PrepareAsync` after `inRunTargets` is computed and before `var candidates`:

```csharp
                // Recommendation 5: an implicit required-lookup rule links to this run's parents.
                // When every target is created earlier in the run, the environment read is skipped.
                var preferInRunColumn = preferInRun?.Contains($"{table}.{column}") == true;
                var readsEnvironment = !(preferInRunColumn
                    && targets.All(t => inRunTargets.Contains(t.ToLowerInvariant(), StringComparer.Ordinal)));
```

and wrap the read loop:

```csharp
                var candidates = new List<LookupRuleValue>();
                if (readsEnvironment)
                {
                    foreach (var target in targets)
                    { /* unchanged */ }
                }
```

Replace the final `preferInRun?.Contains($"{table}.{column}") == true` argument with `preferInRunColumn`. The existing `ordered.Length == 0 && inRunTargets.Length == 0` check still throws for a column with neither source.

`src/SeedBomb.Bulk/BulkCreator.cs`, before `var lookupRun = await PreparedLookupRun.PrepareAsync(`:

```csharp
            progress?.Report(new BulkCreationProgress
            {
                Phase = "Preparing lookups",
                EntityLogicalName = string.Empty,
                BatchIndex = 0,
                TotalBatches = 0,
                RecordsCreated = 0,
                TotalRecords = 0,
            });
```

`src/SeedBomb.Wpf/ViewModels/RunViewModel.cs`, `AcceptProgress`, replace:

```csharp
        if (!string.Equals(StatusHeadline, "Cancelling…", StringComparison.Ordinal))
            StatusHeadline = "Generating…";
```

with:

```csharp
        if (!string.Equals(StatusHeadline, "Cancelling…", StringComparison.Ordinal))
            StatusHeadline = string.IsNullOrEmpty(u.EntityName) ? $"{u.Phase}…" : "Generating…";
```

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~ImplicitRequiredLookupWithInRunParent"`

Expected: passes.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`. The CR-003 tests at lines 816 to 960 that mock `ExistingAccounts` for an explicit `lookupRandom` rule still read the environment (explicit rules are never `PreferInRun`).

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~RunViewModelTests|FullyQualifiedName~RunViewModelLinkPhaseTests"`

Expected: all pass. `AcceptProgress_PhaseOnlySnapshot_LogsNoZeroOfZeroCounts` is unaffected (it asserts the activity line, not the headline).

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Bulk/PreparedLookupRun.cs src/SeedBomb.Bulk/BulkCreator.cs src/SeedBomb.Wpf/ViewModels/RunViewModel.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs src/SeedBomb.Wpf.Tests/RunViewModelTests.cs
git commit -m "Skip environment reads for in-run lookups and label the preparation phase"
```

---

### Task 8: Collapse repeated activity lines and summarise identical batch failures once per table

**Owns:** De-duplication of the activity list (`RunViewModel.AppendActivity`) and of batch-failure log lines (`BulkCreator`), recommendation 6.

**Files:**

- Modify: `src/SeedBomb.Wpf/ViewModels/RunViewModel.cs:1066-1075` (`AppendActivity`).
- Modify: `src/SeedBomb.Bulk/BulkCreator.cs` (`SubmitEntityBatchesAsync`) — per-batch failure at Warning without the stack, one grouped Error line per table.
- Modify: `src/SeedBomb.Wpf.Tests/RunViewModelTests.cs`, `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`.
- Signal: `RunViewModelTests.AppendActivity_CollapsesIdenticalConsecutiveLinesWithACount`.

- [ ] **Step 1: Establish the observable pre-change signal**

```csharp
    [Fact]
    public void ApplyResult_CollapsesIdenticalConsecutiveNoticesWithACount()
    {
        var vm = new RunViewModel();
        vm.StartRun("contoso-dev", seed: 1, plannedTotal: 10, tables: ["account"]);

        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>> { ["account"] = [Guid.NewGuid()] },
            Elapsed = TimeSpan.FromSeconds(1),
            Notices = ["Server busy", "Server busy", "Server busy", "Parallelism capped at 4"],
        }, seed: 1, environmentHost: "contoso-dev");

        Assert.Contains("Server busy (×3)", vm.ActivityLines);
        Assert.DoesNotContain("Server busy", vm.ActivityLines);
        Assert.Contains("Parallelism capped at 4", vm.ActivityLines);
    }
```

`src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`:

```csharp
    [Fact]
    public async Task CreateAsync_IdenticalBatchFailures_AreSummarisedOncePerTable()
    {
        var logger = new RecordingLogger<BulkCreator>();
        var (sut, serviceMock) = BuildSut(logger);
        serviceMock
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault
            {
                ErrorCode = -2147220969,
                Message = "Attribute validation error",
            }));

        var graph = new DependencyGraph();
        graph.AddNode("account");
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 30 },
            BatchSize = 10,
            MaxRetries = 0,
            MaxParallelism = 1,
        };
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = new EntityMetadata { LogicalName = "account" }
        };

        await sut.CreateAsync(config, metadata, graph);

        Assert.Single(logger.Lines, l => l.StartsWith("account: 3 batch(es) (30 rows) failed with: ", StringComparison.Ordinal));
    }
```

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~CollapsesIdenticalConsecutiveNotices"`

Expected: `Assert.Contains() Failure ... Not found: "Server busy (×3)"`.

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~IdenticalBatchFailures"`

Expected: `Assert.Single() Failure: The collection did not contain any matching items`.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Wpf/ViewModels/RunViewModel.cs`. Add a field beside `_activityLog`:

```csharp
    private int _lastActivityRepeats;
```

Replace `AppendActivity`:

```csharp
    private void AppendActivity(string line)
    {
        // Recommendation 6: identical consecutive lines become one line with a count.
        if (_activityLog.Count > 0 && string.Equals(_activityLog[^1].Line, Repeated(line, _lastActivityRepeats), StringComparison.Ordinal))
        {
            _lastActivityRepeats++;
            _activityLog[^1] = new RunActivityRow(Repeated(line, _lastActivityRepeats), "Normal");
        }
        else
        {
            _lastActivityRepeats = 1;
            _activityLog.Add(new RunActivityRow(line, "Normal"));
            if (_activityLog.Count > ActivityLogCap)
                _activityLog.RemoveAt(0);
        }

        RecentActivity.Clear();
        foreach (var row in _activityLog.TakeLast(RecentActivityCap))
            RecentActivity.Add(row);
    }

    private static string Repeated(string line, int count) => count > 1 ? $"{line} (×{count})" : line;
```

Reset `_lastActivityRepeats = 0;` beside `_activityLog.Clear();` in `StartRun` (`RunViewModel.cs:434`), the only reset point.

`src/SeedBomb.Bulk/BulkCreator.cs`, in the per-batch `catch (DataGenerationException ex)` inside `SubmitEntityBatchesAsync`, change the `LogError(ex, ...)` to `LogWarning("Batch {BatchIndex}/{TotalBatches} for {Entity} failed: {Message}", ..., ex.Message)` (no exception object, so no stack per batch). After the batches complete and before `return (allIds, allErrors, failure);`:

```csharp
        // Recommendation 6: one line per distinct failure per table, instead of one per batch.
        foreach (var group in allErrors.Where(e => e.RowIndexes.Count > 1).GroupBy(e => e.ErrorMessage))
            _logger.LogError("{Entity}: {Batches} batch(es) ({Rows} rows) failed with: {Message}",
                entityName, group.Count(), group.Sum(e => e.RowCount), group.Key);
```

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~RunViewModelTests"`

Expected: all pass. `ShowHistorical`/`HydrateFrom` tests at lines 520 to 540 compare `run.ActivityLog` with `ActivityLines`; a saved log has no identical consecutive lines in those fixtures, so they still match. If a fixture does repeat a line, hydrate through a path that bypasses collapsing: add `AppendActivityRaw` used only by `HydrateFrom`.

Run: `dotnet test src/SeedBomb.Bulk.Tests`

Expected: `Passed!`.

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Wpf/ViewModels/RunViewModel.cs src/SeedBomb.Bulk/BulkCreator.cs src/SeedBomb.Wpf.Tests/RunViewModelTests.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs
git commit -m "Collapse repeated activity lines and summarise identical batch failures"
```

---

### Task 9: Expose `MaxRetryCount`, `RetryPauseTime` and `MaxConnectionTimeout` in Settings and the run snapshot

**Owns:** The three SDK knobs as persisted settings (`AppSettings`), their UI, and their appearance in the `[run] snapshot` line (recommendation 7). Batch size and DOP are already in the snapshot.

**Files:**

- Modify: `src/SeedBomb.Wpf/Services/Settings/AppSettings.cs` — three parameters with defaults `0`, `0`, `4`.
- Modify: `src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs:49-64,101-110` — read them when building a client.
- Modify: `src/SeedBomb.Wpf/ViewModels/SettingsViewModel.cs` — three observable fields, load and save.
- Modify: `src/SeedBomb.Wpf/Views/Pages/SettingsPage.xaml:216-219` — an "Advanced: Dataverse client" group of three `ui:NumberBox`.
- Modify: `src/SeedBomb.Bulk/RunDiagnostics.cs:27-45` (`LogSnapshot`).
- Modify: `src/SeedBomb.Wpf.Tests/SettingsViewModelTests.cs`, `src/SeedBomb.Wpf.Tests/DataverseConnectionServiceTests.cs`, `src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`.
- Signal: `DataverseConnectionServiceTests.ClientOptions_ComeFromSettings`.

- [ ] **Step 1: Establish the observable pre-change signal**

`src/SeedBomb.Wpf.Tests/DataverseConnectionServiceTests.cs`:

```csharp
    [Fact]
    public async Task ClientOptions_ComeFromSettings()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppSettings.Default with { SdkMaxRetryCount = 2, SdkRetryPauseSeconds = 7, ConnectionTimeoutMinutes = 6 });

        var options = await DataverseConnectionService.ClientOptionsAsync(settings.Object, CancellationToken.None);

        Assert.Equal(2, options.MaxRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(7), options.RetryPauseTime);
        Assert.Equal(TimeSpan.FromMinutes(6), options.ConnectionTimeout);
    }

    [Fact]
    public async Task ClientOptions_DefaultToAppOwnedRetries_WhenSettingsAreUnavailable()
    {
        var options = await DataverseConnectionService.ClientOptionsAsync(null, CancellationToken.None);

        Assert.Equal(0, options.MaxRetryCount);
        Assert.Equal(TimeSpan.Zero, options.RetryPauseTime);
        Assert.Equal(TimeSpan.FromMinutes(4), options.ConnectionTimeout);
    }
```

`src/SeedBomb.Wpf.Tests/SettingsViewModelTests.cs`:

```csharp
    [Fact]
    public async Task SaveAsync_PersistsTheDataverseClientKnobs()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppSettings.Default);
        AppSettings? saved = null;
        settings.Setup(s => s.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);
        var vm = new SettingsViewModel(settings.Object, Mock.Of<ILogger<SettingsViewModel>>());
        await vm.LoadCommand.ExecuteAsync(null);

        vm.SdkMaxRetryCount = 3;
        vm.SdkRetryPauseSeconds = 10;
        vm.ConnectionTimeoutMinutes = 8;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(saved);
        Assert.Equal(3, saved.SdkMaxRetryCount);
        Assert.Equal(10, saved.SdkRetryPauseSeconds);
        Assert.Equal(8, saved.ConnectionTimeoutMinutes);
    }
```

`src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs`, in `CreateAsync_LogsOneSnapshot_PhaseIntervals_AndOneTimingRecordPerBatch`, add after the snapshot assertion:

```csharp
        Assert.Contains(lines, l => l.StartsWith("[run] snapshot:", StringComparison.Ordinal)
            && l.Contains("SDK retries n/a, SDK retry pause n/a, connection timeout ", StringComparison.Ordinal));
```

(The mock is not a `ServiceClient`, so the instance values read `n/a`; the static timeout is always printed.)

- [ ] **Step 2: Capture the current signal**

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~ClientOptions|FullyQualifiedName~PersistsTheDataverseClientKnobs"`

Expected: compile errors `CS0117 'AppSettings' does not contain a definition for 'SdkMaxRetryCount'` and `CS0117 ... 'ClientOptionsAsync'`.

- [ ] **Step 3: Implement the minimum fix**

`src/SeedBomb.Wpf/Services/Settings/AppSettings.cs`: three new positional parameters after `RunDiagnostics`, each with a `/// <param>` line, and clamps in `Clamped()`:

```csharp
/// <param name="SdkMaxRetryCount">Retries the Dataverse SDK performs on its own before a fault reaches the app. 0 (default) leaves every retry to the app.</param>
/// <param name="SdkRetryPauseSeconds">Base pause between SDK retries, in seconds. Unused when <paramref name="SdkMaxRetryCount"/> is 0.</param>
/// <param name="ConnectionTimeoutMinutes">HTTP timeout for one Dataverse call, in minutes. The SDK default is 4.</param>
public record AppSettings(
    int DefaultRecordCount = GenerationLimits.DefaultRecordCount,
    int DefaultBatchSize = GenerationLimits.DefaultBatchSize,
    int DefaultDop = 0,
    bool DarkTheme = false,
    string PaletteId = Theme.DesignThemeManager.DefaultPaletteId,
    bool KeepRunSheetOpen = true,
    bool RunDiagnostics = false,
    int SdkMaxRetryCount = 0,
    int SdkRetryPauseSeconds = 0,
    int ConnectionTimeoutMinutes = 4)
```

```csharp
        DefaultDop = Math.Clamp(DefaultDop, 0, GenerationLimits.MaxDop),
        SdkMaxRetryCount = Math.Clamp(SdkMaxRetryCount, 0, 10),
        SdkRetryPauseSeconds = Math.Clamp(SdkRetryPauseSeconds, 0, 300),
        ConnectionTimeoutMinutes = Math.Clamp(ConnectionTimeoutMinutes, 1, 30),
```

`src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs`: a record and a static reader beside `SdkLoggerAsync`, then use it in the construction.

```csharp
    /// <summary>What a new <see cref="ServiceClient"/> is built with. From settings; defaults keep retries in the app.</summary>
    /// <param name="MaxRetryCount">SDK retries per call.</param>
    /// <param name="RetryPauseTime">Base SDK pause between retries.</param>
    /// <param name="ConnectionTimeout">HTTP timeout per call; set on the static <see cref="ServiceClient.MaxConnectionTimeout"/> before construction.</param>
    public sealed record ClientOptions(int MaxRetryCount, TimeSpan RetryPauseTime, TimeSpan ConnectionTimeout);

    /// <summary>Reads the client options; an unavailable settings store yields the defaults.</summary>
    /// <param name="settings">Settings store, or null.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<ClientOptions> ClientOptionsAsync(ISettingsService? settings, CancellationToken ct)
    {
        var s = AppSettings.Default;
        if (settings is not null)
        {
            try { s = (await settings.LoadAsync(ct).ConfigureAwait(false)).Clamped(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* defaults */ }
        }
        return new ClientOptions(s.SdkMaxRetryCount, TimeSpan.FromSeconds(s.SdkRetryPauseSeconds), TimeSpan.FromMinutes(s.ConnectionTimeoutMinutes));
    }
```

In `GetOrganizationServiceAsync`, after `var sdkLogger = ...`:

```csharp
            var options = await ClientOptionsAsync(_settings, ct).ConfigureAwait(false);
            // Static and read only at construction (SDK contract); set it right before building.
            ServiceClient.MaxConnectionTimeout = options.ConnectionTimeout;
```

and in the initializer replace the two literals from Task 1:

```csharp
                MaxRetryCount = options.MaxRetryCount,
                RetryPauseTime = options.RetryPauseTime,
```

`src/SeedBomb.Wpf/ViewModels/SettingsViewModel.cs`: three fields, loaded in `LoadAsync`, saved in `SaveAsync`.

```csharp
    [ObservableProperty] private int _sdkMaxRetryCount;
    [ObservableProperty] private int _sdkRetryPauseSeconds;
    [ObservableProperty] private int _connectionTimeoutMinutes = 4;
```

```csharp
            SdkMaxRetryCount = s.SdkMaxRetryCount;
            SdkRetryPauseSeconds = s.SdkRetryPauseSeconds;
            ConnectionTimeoutMinutes = s.ConnectionTimeoutMinutes;
```

```csharp
            var settings = new AppSettings(
                DefaultRecordCount, DefaultBatchSize, DefaultDop,
                DarkTheme, PaletteId, KeepRunSheetOpen, RunDiagnostics,
                SdkMaxRetryCount, SdkRetryPauseSeconds, ConnectionTimeoutMinutes).Clamped();
```

`src/SeedBomb.Wpf/Views/Pages/SettingsPage.xaml`: after the "Run diagnostics" `Grid` and before the trailing parallelism `TextBlock`, a group following the existing `WrapPanel` of `NumberBox` fields:

```xml
                        <TextBlock Style="{DynamicResource DG.GroupLabel}" Text="Advanced: Dataverse client" Margin="0,20,0,0" />
                        <TextBlock Style="{DynamicResource DG.Tertiary}" TextWrapping="Wrap" Margin="0,2,0,0"
                                   Text="SeedBomb retries throttled and failed calls itself. Leave SDK retries at 0 unless support asks otherwise. Changes apply to the next connection." />
                        <WrapPanel Margin="0,8,0,0">
                            <StackPanel MinWidth="160" Width="220" Margin="0,0,16,8">
                                <TextBlock Style="{DynamicResource DG.GroupLabel}" Text="SDK retries" />
                                <ui:NumberBox Value="{Binding SdkMaxRetryCount, Mode=TwoWay}" Minimum="0" Maximum="10"
                                              Height="{DynamicResource DG.Size.Control}" AutomationProperties.Name="SDK retries" />
                            </StackPanel>
                            <StackPanel MinWidth="160" Width="220" Margin="0,0,16,8">
                                <TextBlock Style="{DynamicResource DG.GroupLabel}" Text="SDK retry pause (s)" />
                                <ui:NumberBox Value="{Binding SdkRetryPauseSeconds, Mode=TwoWay}" Minimum="0" Maximum="300"
                                              Height="{DynamicResource DG.Size.Control}" AutomationProperties.Name="SDK retry pause in seconds" />
                            </StackPanel>
                            <StackPanel MinWidth="160" Width="220" Margin="0,0,0,8">
                                <TextBlock Style="{DynamicResource DG.GroupLabel}" Text="Call timeout (min)" />
                                <ui:NumberBox Value="{Binding ConnectionTimeoutMinutes, Mode=TwoWay}" Minimum="1" Maximum="30"
                                              Height="{DynamicResource DG.Size.Control}" AutomationProperties.Name="Call timeout in minutes" />
                            </StackPanel>
                        </WrapPanel>
```

`src/SeedBomb.Bulk/RunDiagnostics.cs`, `LogSnapshot`: extend the message and arguments.

```csharp
            "[run] snapshot: app {AppVersion}, Dataverse.Client {SdkVersion}, Caching.Memory {CacheVersion}, batch {BatchSize} (starting at {StartBatch}), configured DOP {ConfiguredDop}, SDK DOP hint {DopHint}, affinity cookie {AffinityCookie}, SDK retries {SdkRetries}, SDK retry pause {SdkPause}, connection timeout {Timeout}, first-attempt API {Apis}",
```

with, after the affinity-cookie argument:

```csharp
            sc is null ? "n/a" : sc.MaxRetryCount.ToString(),
            sc is null ? "n/a" : sc.RetryPauseTime.ToString(),
            ServiceClient.MaxConnectionTimeout,
```

(`StartBatch` is Task 6's argument; if Task 9 lands first, omit that clause and add it in Task 6.)

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `dotnet test src/SeedBomb.Wpf.Tests --filter "FullyQualifiedName~ClientOptions|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~DataverseConnectionServiceTests"`

Expected: all pass.

Run: `dotnet test src/SeedBomb.Bulk.Tests --filter "FullyQualifiedName~LogsOneSnapshot"`

Expected: passes with the extended snapshot assertion.

Run: `dotnet build SeedBomb.slnx`

Expected: `Build succeeded.`, `0 Warning(s)` (the three new `AppSettings` parameters have `<param>` docs).

- [ ] **Step 5: Commit the coherent change**

```bash
git add src/SeedBomb.Wpf/Services/Settings/AppSettings.cs src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs src/SeedBomb.Wpf/ViewModels/SettingsViewModel.cs src/SeedBomb.Wpf/Views/Pages/SettingsPage.xaml src/SeedBomb.Bulk/RunDiagnostics.cs src/SeedBomb.Wpf.Tests/SettingsViewModelTests.cs src/SeedBomb.Wpf.Tests/DataverseConnectionServiceTests.cs src/SeedBomb.Bulk.Tests/BulkCreatorTests.cs
git commit -m "Expose the Dataverse client retry and timeout knobs in Settings and the run snapshot"
```

---

### Task 10: Write-path change checklist in the developer docs

**Owns:** Recommendation 9 as a repository artefact: the live-smoke step before merging a write-path change, and tagging before a review batch.

**Files:**

- Modify: `documentation/how-to/developer/diagnostics.md` — new final section.
- Signal: the section exists; `grep` finds it.

- [ ] **Step 1: Establish the observable pre-change signal**

Run: `grep -n "Before merging a write-path change" documentation/how-to/developer/diagnostics.md`

- [ ] **Step 2: Capture the current signal**

Expected: no output, exit code 1.

- [ ] **Step 3: Implement the minimum fix**

Append to `documentation/how-to/developer/diagnostics.md`:

```markdown
## 5. Before merging a write-path change

Any change under `src/SeedBomb.Bulk` or `src/SeedBomb.Wpf/Services/Dataverse` is verified against a live environment before it merges. Unit tests cannot see nested retries, `Retry-After` growth or execution-time throttling.

1. Tag the commit you are about to build on (`git tag pre-<topic>`), so a user can go back.
2. Turn on **Run diagnostics** in Settings.
3. Run a few thousand rows on one standard table and one custom table with parallelism left at automatic.
4. Paste the `[run] snapshot` line and the `[timing] batch` lines from the app log into the pull request, with the rejected-row count from the run sheet.
5. A `[timing] batch` line whose `attempts` is above 1 while `total` is in minutes and `last call` is in seconds means something is sleeping inside an attempt. Find it before merging.
```

- [ ] **Step 4: Confirm the signal changed, and nothing else did**

Run: `grep -n "Before merging a write-path change" documentation/how-to/developer/diagnostics.md`

Expected: one line, `## 5. Before merging a write-path change`.

- [ ] **Step 5: Commit the coherent change**

```bash
git add documentation/how-to/developer/diagnostics.md
git commit -m "Document the live smoke required before merging a write-path change"
```

---

## After the plan

- `DeferredLookupBackfill.SubmitOrganizationRequestBatchesAsync` and `BulkCreator.SubmitEntityBatchesAsync` both construct a `BatchError` from a `DataGenerationException` with `ThrottlePolicy.IsTransient(...)`; that duplication predates this plan and is left as is.
- `RejectionClassifier.IsRetryable` still matches `"429"` in the message text and the three fault codes; after Task 1 `BatchError.IsTransient` already covers both, so the two extra clauses are redundant but harmless. Removing them is outside this plan.
- Finding F7 (a test that the token path never takes `RunSessionGate`) is a one-test follow-up in `RunSessionGateTests`.
