# SeedBomb generation regression investigation

Window investigated: the last commit before September 28th (`16c3e75`, Sept 27 18:20 EDT, "Remove explicit TextBox Height") through `00e6c41` (Oct 7, merge of the Dependabot PRs). Because the September 28th symptom date is a user observation and not a commit boundary, the September 26th review batch that landed two days earlier was also examined, and the post-`00e6c41` "Phase 1 to 3" fixes on this branch were checked to see whether they address what was found.

Method: `git diff 16c3e75 00e6c41` over `src/`, per-commit reads of every commit touching `SeedBomb.Bulk`, `Services/Dataverse`, `Services/Auth`, `RunViewModel` and the package props, and a read of the Dataverse `ServiceClient` 1.2.x source for its retry and timeout defaults. No live environment was available, so nothing here was reproduced against Dataverse. Where a conclusion is inference rather than something read in the code, it says so.

## 1. Summary

The timeouts and the flood of HTTP errors are not one bug. They are the compound effect of four changes that each looked reasonable in isolation:

| # | Change | Commit | Date | Effect |
| --- | --- | --- | --- | --- |
| 1 | `ServiceClient` built-in retries were turned back on, at the SDK default of **10 retries** | `41963f6` (CR-001) | Sept 26 | Every throttled or 502/503 call now sleeps and resends inside the SDK, up to 10 times, honouring `Retry-After` (minutes). The app's own `ThrottlePolicy` still wraps this with 3 more retries. |
| 2 | `ThrottlePolicy` started retrying `OperationCanceledException` as a "transport timeout" | `eccf60a` (IN-004) | Sept 29 | A `CreateMultiple` that hits the SDK's 4 minute HTTP timeout is resent up to 3 more times, at 4 minutes each, even though the server may already have committed it. |
| 3 | Required-lookup preflight adds implicit `lookupRandom` rules and reads candidates from the environment before the first write | `ee85321`, `72e80ab` (CR-003), `23970cf` | Oct 4 to 5 | More pre-run `RetrieveMultiple` paging per run, all under the same nested retry stack. |
| 4 | Failures that used to be swallowed are now logged, recorded and shown | `99e0809` (IN-001), `41854ae` (IN-007), `73a1595` (WR-006), `f14b561` (CR-002) | Sept 28 to Oct 4 | The run sheet, History and the log now show every batch and row failure, with the SDK's HTTP text in each message. |

Change 1 is the root cause of the "hangs" that read as timeouts, and change 2 turns real timeouts into very long stalls. Changes 3 and 4 are why the problem is so loud. The pre-existing `CreateMultiple` to `ExecuteMultiple` fallback multiplies all of it.

Before September 26th the app failed fast: SDK retries were off (`MaxRetryCount = 0`), a throttle fault was terminal, and an HTTP timeout was mis-reported as a user cancel. That was not healthier, but it was fast and quiet, which is what "it worked" looked like from the UI.

The Phase 1 to 3 commits on this branch (Oct 28 to Nov 1) fix the DOP hint, the metadata cache, wrapped socket faults and logging. None of them touches the nested retry stack, the timeout resend, or the fallback amplification, so the symptom surviving those fixes is expected.

## 2. What the pipeline looked like at each point

### Before September 26th (`cdb40f7`, Sept 25)

- `DataverseConnectionService` built the client with `MaxRetryCount = 0`, `RetryPauseTime = TimeSpan.Zero`, `EnableAffinityCookie = false`, with the comment "Disable built-in retries, BulkCreator's ThrottlePolicy owns retry logic". This dates from the initial import on September 16th (PR 20).
- `ThrottlePolicy` retried only `TimeoutException`, `HttpRequestException`, `IOException` and a `ProtocolException` whose text contained `(429)`. Any `FaultException<OrganizationServiceFault>` was terminal. Its header comment already claimed "ServiceClient already pauses and resends on service-protection limits", which was false while `MaxRetryCount` was 0. So in this state **nobody retried a service-protection fault**. A throttled batch failed immediately and its rows went to the rejected list.
- `OperationCanceledException` was rethrown unconditionally. An HttpClient timeout (which surfaces as `TaskCanceledException`) therefore ended the run as "Cancelled".
- Default DOP 8, batch size 500, 3 app retries.

### At the baseline (`16c3e75`, Sept 27) after the September 26th batch

- `41963f6` (CR-001) removed `MaxRetryCount = 0` and `RetryPauseTime = TimeSpan.Zero`, with the comment "keep the SDK's own retries, they honour Retry-After on service-protection faults, which ThrottlePolicy deliberately does not retry". It did not set a value, so the SDK default applies. In `Microsoft.PowerPlatform.Dataverse.Client` 1.2.x that default is `MaxRetryCount = 10` and `RetryPauseTime = 5 s` (`ConfigurationOptions.cs`, `ApiOperationRetryCountOverride` default 10). The SDK retries 429 throttle codes using `Retry-After`, plus 502, 503 and SQL transient errors, with `5 s + 2^n` backoff when no header is present.
- `c9953dd` (WR-003) narrowed `RejectionClassifier.IsRetryable` to the three service-protection fault codes plus "429" in the message.
- `ca14b73` (WR-002) made a cancelled run return the rows already written instead of throwing.
- `933b646` built the `ServiceClient` on the thread pool. `7392d4a` gave each OAuth profile its own MSAL account. `85d573d` made the signed-in profile the owner of the live connection and dropped the connection service's `ProfilesChanged` reset. These are sound and are not implicated.

### Between the baseline and `00e6c41` (Sept 28 to Oct 7)

Generation-path commits, in order:

- `7ea027b` (WR-008, Sept 28): parallelism clamped to a single 16 cap. Later `69dcfb2` (WR-014, Oct 4) made `GenerationLimits` the owner (MaxBatchSize 1000, MaxDop 16, MaxRecordCount 100,000) and `BulkCreator.CreateAsync` now throws on out-of-range config.
- `eccf60a` (IN-004, Sept 29): `ThrottlePolicy` now catches `OperationCanceledException` when the caller's token is not cancelled and retries it as a timeout. `IsTransient` reports it as transient so the rows are offered for retry.
- `136fc9b` (IN-006, Sept 29): a `ServiceClient` that is not `IsReady` is disposed and never cached. Good.
- `99e0809` (IN-001, Sept 28), `41854ae` (IN-007, Sept 29): unobserved task exceptions, AppDomain failures and formerly swallowed dialog and auth failures are now written to the crash log and app log.
- `a7e8b86` (CR-001, Oct 4): `RunSessionGate`. `RunViewModel` acquires the gate before `GenerateAsync` and holds it for the whole run. Sign-in, sign-out, profile reconcile and startup all wait on it.
- `ee85321`, `72e80ab`, `23970cf` (CR-003 and amendment, Oct 4 to 5): `RequiredLookupPreflight` runs before any write; every SystemRequired lookup whose targets are created earlier in the run gets an implicit `LookupRandomRule`; `PreparedLookupRun.PrepareAsync` reads existing candidates for every target of every `lookupRandom` column (up to 1,000 rows per target, 500 per page) before the first write, even when the column is marked `PreferInRun`.
- `22a5920`, `f14b561` (CR-002, Oct 4): alternate keys scoped to a run hash; retry regenerates exactly the rejected row indexes. `BatchError` lost its batch index and gained `RowIndexes` and `IsTransient`.
- `ff8fdb1` (Oct 4): Retry offered for network faults, not only throttling.
- `7488e3a` (WR-015, Oct 4): NuGet audit warnings are now errors; `System.Security.Cryptography.Xml` pinned to 10.0.10 for `net10.0` only.
- `80debdf` (Sept 30): Blazor project removed. Not implicated.

### After `00e6c41`, on this branch (Oct 28 to Nov 1)

- `256faa9`: `ThrottlePolicy` retries `CommunicationException` wrapping `HttpRequestException` or `IOException` (socket reset 10054). Correct, but it adds another retried class on top of the SDK's 10.
- `35f4d23` (Phase 1): metadata cache no longer disposed under in-flight readers; one transient classification; every retry logged with duration.
- `d4d42ec` (Phase 2): per-batch timing lines, run snapshot, opt-in SDK warning logger.
- `7cb61b5` (Phase 3) and `837d63b`: DOP resolved per table from `x-ms-dop-hint`; an explicit DOP is capped at the hint.

The retry stack, the timeout resend and the fallback are unchanged at HEAD. `ServiceClient` is still constructed without `MaxRetryCount`, so it is still 10.

## 3. Findings, ranked by contribution to the symptom

### F1. Two retry owners, nested, with the SDK's default of 10 (root cause)

Before: SDK 0 retries, app 3 retries, throttle faults terminal.

After `41963f6`: SDK 10 retries inside each app attempt. Per batch, the worst case is now:

```
ThrottlePolicy attempt (x4)
  └─ ServiceClient CreateMultiple attempt (x11, each pausing Retry-After or 5 s + 2^n)
then, because CreateMultiple surfaced a FaultException, BulkCreator.SubmitBatchAsync falls back to
ThrottlePolicy attempt (x4)
  └─ ServiceClient ExecuteMultiple attempt (x11), each carrying 500 Create sub-requests
```

That is up to 44 `CreateMultiple` sends followed by up to 44 `ExecuteMultiple` sends of the same 500 rows, with up to eight of these batch loops running concurrently at DOP 8. Dataverse extends `Retry-After` while a client keeps hammering a throttled server ("If the application continues to send such demanding requests, the duration is extended"), so each sleep gets longer. From the run sheet this is an indeterminate stall of many minutes followed by a burst of errors, which is exactly "timeouts and HTTP error spam".

Why `ThrottlePolicy` still retries around the SDK: it treats `FaultException` as terminal, which is right, but the SDK converts some of its own exhausted retries into `ProtocolException` text containing `(429)` or `(503)`, which `ThrottlePolicy` does retry. `TimeoutException`, `HttpRequestException` and `IOException` are also caught by both layers in different forms.

Evidence: `src/SeedBomb.Wpf/Services/Dataverse/DataverseConnectionService.cs` at `00e6c41` lines 72 to 80 (no `MaxRetryCount`), `src/SeedBomb.Bulk/ThrottlePolicy.cs` (3 retries default via `GenerationConfig.MaxRetries`), `src/SeedBomb.Bulk/BulkCreator.cs` `SubmitBatchAsync` (fallback on any `FaultException`), SDK `ConfigurationOptions.cs` line 41 (default 10) and `ServiceClient.ShouldRetry` (throttle codes, 502, 503, SQL).

### F2. HTTP timeouts are resent blindly (turns a slow batch into a 16 minute stall and possible duplicates)

`ServiceClient.MaxConnectionTimeout` defaults to 4 minutes and is applied to the HttpClient. The SDK does not retry a timeout on the Web API path (`Command_WebExecuteAsync` only retries `HttpOperationException`), so it surfaces as `TaskCanceledException`. Since `eccf60a` (IN-004) `ThrottlePolicy` catches that as a timeout and retries up to 3 times. Each attempt can take the full 4 minutes, so one 500 row batch that is slow (plug-ins, large table, a throttled server that is still processing) can hold a DOP slot for 16 minutes before it is reported.

`CreateMultiple` on a standard table is all or nothing, but a request that timed out on the client may still commit on the server. Resending it creates duplicate rows that the alternate-key scoping (CR-002) will then reject as duplicate keys on tables that have keys, and silently duplicate on tables that do not. The pre-September-29 behaviour (report the run as cancelled) was wrong too, but it did not resend.

### F3. The `CreateMultiple` to `ExecuteMultiple` fallback amplifies every fault (pre-existing, now reachable far more often)

`SubmitBatchAsync` falls back to `ExecuteMultiple` on **any** `FaultException`, not just the "message unsupported" code `0x80040800`. Standard-table `CreateMultiple` rolls back the whole batch on one bad row, so a data fault (a required lookup, a plug-in rejection, a duplicate key) is replayed as 500 individual `Create` sub-requests that fail one by one. That is where "500 identical error lines for one table" comes from in the rejected list, and it spends 500 units of the 1,200 second per 5 minute execution budget on requests that are known to fail. After F1 the fallback is reached far more often because the SDK now surfaces a throttle fault only after it has retried 10 times.

### F4. Pre-run environment reads grew (CR-003) and share the same retry stack

`PreparedLookupRun.PrepareAsync` now runs for implicit rules too. For each `lookupRandom` column it reads every target table (up to 1,000 rows, 2 pages) through `ThrottlePolicy`, and it does so even when the column is `PreferInRun` and the in-run pool will be used. It also issues a `RetrieveEntityRequest` for any target not already in the metadata set. On a profile with many tables and polymorphic lookups (`customer`, `regarding`) this is dozens of calls before the first write, each able to enter the F1 loop, and the progress phase shows only "Generating records". This is a contributing cause of "it sits there before anything happens", not the main one.

### F5. Visibility changed as much as behaviour did

- `BatchError` now carries the SDK's full message per failed sub-request and the activity log shows each one.
- IN-001 logs every unobserved task exception to the crash log. `ServiceClient` faults internal tasks during dispose and during retry bookkeeping, so the crash log fills with SDK noise that previously went nowhere.
- IN-007 and WR-006 surface dialog, auth and terminal failures that used to be silent.
- Phase 2 adds a `[timing]` line per batch and, with the opt-in diagnostics setting, SDK warnings.

None of this created the failures, but it is why the September 28th build looked like it had suddenly started "spamming HTTP errors". A reader of the log cannot tell a 10th SDK retry from a new failure because the SDK's own retry log lines are not in the app log (they go to the SDK's `TraceSource` unless the opt-in logger is on).

### F6. DOP was wrong for the first table and ignored the hint until Phase 3 (fixed, but it mattered in the window)

At `00e6c41` the run started at DOP 8 regardless of `x-ms-dop-hint`, adopted the hint only after the first table finished, and did the WhoAmI warmup only at 5,000 or more rows. Many environments hint 4 or lower. Eight concurrent 500 row `CreateMultiple` requests on a 4-hint environment is the quickest way to trip the execution-time limit, which is the throttle with the longest `Retry-After`. Phase 3 (`7cb61b5`) and `837d63b` fix this on the branch; keep them.

### F7. The run holds `RunSessionGate` for its whole duration (risk, not a cause)

`RunViewModel` acquires the gate before `GenerateAsync` and releases it in the outer `finally`. `ProfileAuthService.ReconcileSessionAsync`, `SettingsViewModel.SignOutAsync` and app startup also take it. The token provider does not, so writes do not deadlock today. But any future change that makes something in the write path take the gate will hang the run with no error, and a reconcile queued during a long run will fire the moment the run ends. Document the invariant in `RunSessionGate` and add a test that the token path never acquires it.

### F8. Things that were checked and are not implicated

- `EnableAffinityCookie = false` has been there since the import and is Microsoft's recommendation for throughput.
- `useUniqueInstance: true` and the token provider are unchanged in behaviour. `GetTokenAsync` calls MSAL `AcquireTokenSilent` per request, as before.
- `DataverseMetadataService` caching is unchanged between the baseline and `00e6c41` (30 minute `MemoryCache`); Phase 1 fixed a dispose-under-reader bug that was already present.
- `ResetAsync` is only called from sign-in, switch and sign-out, all of which are gated during a run.
- The `System.Security.Cryptography.Xml` pin and the Blazor removal do not touch the write path.
- `028d5a4` ("bounded poll") changed only a test.
- Package versions: `Microsoft.PowerPlatform.Dataverse.Client` stayed at 1.2.27 throughout.

## 4. Opinion

The September 26th to October 5th batch was roughly seventy review-ticket commits (WR, CR, IN, PR series) landed in ten days, almost all of them correctness and safety fixes argued from the code rather than from a run. Individually they are good engineering; several fix real data-safety holes (returning written rows on cancel, scoped alternate keys, never caching a failed client). The Phase 2 commit message of October 30th says "Found in the first live run", which suggests that was the first live run since the batch started. Four weeks of reliability work on a Dataverse writer without a Dataverse run is how a retry policy ends up with two owners and a 44 attempt worst case.

The specific judgement call that went wrong is CR-001's premise: "keep the SDK's own retries, they honour Retry-After". The SDK does honour it, but silently, for ten attempts, inside a call the UI cannot see into, and the app kept its own retries on top. The original import had the right architecture (one retry owner, the app) and the wrong implementation (it never honoured `Retry-After`). CR-001 picked the other owner instead of fixing the implementation.

IN-004 is the second misjudgement. Treating an unrequested cancel as a retryable timeout is correct for idempotent reads and wrong for a 500 row create. The pre-existing behaviour was a bug, but the fix chose the more dangerous of the two bad options.

Nothing here makes the software "nearly worthless". The generator, graph, rules and UI are fine. The write path is misconfigured in a way that is a few hundred lines to put right.

## 5. Recommendations

Ordered by expected effect per hour of work.

1. **One retry owner.** Set `MaxRetryCount = 0` on the `ServiceClient` again, and make `ThrottlePolicy` honour `Retry-After` from `OrganizationServiceFault.ErrorDetails["Retry-After"]` (a `TimeSpan`) for the three service-protection codes (`-2147015902`, `-2147015903`, `-2147015898`). Keep the existing exponential backoff for 502, 503 and network faults. Surface the wait in the run sheet ("Server busy, resuming in 47 s") and make it cancellable. This alone removes the silent multi-minute stalls. If you would rather keep the SDK as owner, set `MaxRetryCount` to 2 or 3 and remove `ProtocolException` and `TimeoutException` from `ThrottlePolicy`, so no error is retried twice.

2. **Restrict the fallback to the unsupported-message case.** In `SubmitBatchAsync`, fall back to `ExecuteMultiple` only on `0x80040800`. For any other fault, record one `BatchError` for the batch with the fault message and its `RowIndexes`, and move on. This stops 500-line error floods and stops spending execution budget on requests known to fail.

3. **Stop resending on client timeout.** On `TaskCanceledException` from a `CreateMultiple`, do not resend the same batch. Either split it (halve `BatchSize` for that table and resubmit the halves) or, now that Phase 2 has the `modifiedon` watermark reconcile, count the table's rows since the watermark before deciding. Report rows as "unknown outcome" rather than "rejected" when neither is possible. Consider raising `ServiceClient.MaxConnectionTimeout` (static, set before construction) only after batch size is adaptive.

4. **Adaptive batch size and a shared throttle gate.** Start at the hint for DOP (already done on this branch) and at 200 to 250 rows for `CreateMultiple`, growing toward the configured size while calls stay under about 20 seconds. When any batch receives a service-protection fault, pause the whole table's `Parallel.ForEachAsync` for the `Retry-After` through one shared gate instead of letting eight batches sleep and resend independently. The thundering herd is what makes `Retry-After` grow.

5. **Trim pre-run reads.** In `PreparedLookupRun.PrepareAsync`, skip `ReadTargetAsync` for a `PreferInRun` column whose in-run targets all have a positive planned count, and add a "Preparing lookups" progress phase so the stall is at least labelled.

6. **De-duplicate the log and the activity list.** Collapse identical consecutive errors into one line with a count, and keep SDK retry chatter behind the existing diagnostics toggle. Keep Phase 2's `[timing]` lines; they are the only way to see F1 happening.

7. **Expose the knobs.** `MaxRetryCount`, `RetryPauseTime`, `MaxConnectionTimeout`, batch size and DOP should appear in the run snapshot log line and, for the first three, in an advanced Settings section, so a user can report them with a log.

8. **Test the retry budget.** Add a `BulkCreatorTests` case with a fake `IOrganizationServiceAsync2` that returns a throttle fault with a `Retry-After` of 1 second for the first N calls, and assert total attempts and total elapsed time are bounded. Today nothing asserts an upper bound on attempts.

9. **Process.** Run one live smoke (a few thousand rows on a standard table and a custom table, at automatic DOP) before merging any batch that touches `SeedBomb.Bulk` or `Services/Dataverse`, and record the `[timing]` output in the PR. Tag before a review batch so a user can go back.

## 6. How to confirm this on a real environment

1. Turn on the "Run diagnostics" setting added in Phase 2.
2. Run 5,000 rows of one table with automatic DOP and the default 500 batch size.
3. In the log, look at `[timing] batch` lines: `attempts` greater than 1 with `total time` in minutes while `last call` is seconds is F1 (SDK sleeping inside the app's attempt). A `last call` near 240,000 ms is F2.
4. Count rejected rows in the summary against the reconcile count. Reconcile higher than acknowledged means a timed-out batch committed and was resent (F2).
5. Repeat with `MaxRetryCount = 0` on the client as a one-line experiment. If the run finishes faster with the same or fewer rejected rows, F1 is confirmed.

## 7. Commit reference

| Commit | Date | Subject |
| --- | --- | --- |
| `2328937` | 2026-09-16 | Initial WPF import (PR 20): `MaxRetryCount = 0`, app owns retries |
| `41963f6` | 2026-09-26 | Let ServiceClient retry throttled requests and keep batch fault codes (CR-001) |
| `c9953dd` | 2026-09-26 | Classify only service-protection faults as retryable (WR-003) |
| `ca14b73` | 2026-09-26 | Return rows already written when a run is cancelled (WR-002) |
| `16c3e75` | 2026-09-27 | Baseline used for this investigation |
| `7ea027b` | 2026-09-28 | Clamp configured parallelism to one 16 cap (WR-008) |
| `99e0809` | 2026-09-28 | Log unobserved task exceptions and AppDomain failures (IN-001) |
| `eccf60a` | 2026-09-29 | Retry transport-timeout cancellations instead of reporting them as user cancels (IN-004) |
| `136fc9b` | 2026-09-29 | Dispose and never cache a ServiceClient that failed to connect (IN-006) |
| `41854ae` | 2026-09-29 | Log swallowed run and auth failures (IN-007) |
| `69dcfb2` | 2026-10-04 | Run-size bounds owned by GenerationLimits (WR-014) |
| `a7e8b86` | 2026-10-04 | Tie run targets and Retry to a protected write-start session (CR-001) |
| `ee85321` | 2026-10-04 | Preflight required lookups before any write (CR-003) |
| `72e80ab` | 2026-10-04 | Let a required lookup proceed when this run creates its target first |
| `22a5920`, `f14b561` | 2026-10-04 | Scoped alternate keys; retry exactly the rejected rows (CR-002) |
| `23970cf` | 2026-10-05 | Prefer in-run parents for implicit lookups |
| `00e6c41` | 2026-10-07 | End of the investigated window |
| `256faa9` | 2026-10-28 | Retry wrapped socket resets |
| `35f4d23` | 2026-10-30 | Phase 1 reliability fixes |
| `d4d42ec` | 2026-10-30 | Phase 2 observability |
| `7cb61b5`, `837d63b` | 2026-10-30, 11-01 | Phase 3 DOP from the server hint |
