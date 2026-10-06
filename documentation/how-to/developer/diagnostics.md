# Diagnose local application failures

Use this guide to collect a reproducible failure and identify the code or test area to investigate. You need access to the Windows user account that ran SeedBomb. Do not clear its app data as a first troubleshooting step.

## 1. Capture the failure

Record the app version, Windows version, action that triggered the failure, time of the failure, and the complete visible error message. For a generation problem, also record the table, record count, relevant rules, batch settings, and whether any rows were created.

Reproduce with the smallest configuration that still fails. Use a non-production Dataverse environment when reproduction writes records; repeated runs can create additional rows.

## 2. Find the diagnostic files

The usual data directory is `%LOCALAPPDATA%\SeedBomb`:

```powershell
Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\SeedBomb\logs" -Filter 'seedbomb-*.log'
Get-Content -LiteralPath "$env:LOCALAPPDATA\SeedBomb\startup-error.log" -Tail 100
```

| File | What it contains |
| --- | --- |
| `logs\seedbomb-yyyyMMdd.log` | Timestamped application log lines with severity, category, and exception details where supplied |
| `startup-error.log` | Appended crash and unhandled-exception details, including failures after startup |

Daily logs are pruned on provider creation when their last-write time is more than seven days old. Capture the relevant log promptly. The crash file is appended rather than overwritten.

If the files do not exist, inspect the failure message and the directory's availability. Logging is best effort and a failure to write a log does not necessarily produce a second error.

When upgrading from the old DataGen name, [AppPaths](../../../src/SeedBomb.Wpf/Services/Diagnostics/AppPaths.cs) attempts to move `%LOCALAPPDATA%\DataGen` to `SeedBomb` if the new directory does not exist. If the move fails, the app continues using `DataGen` and reports a warning. In that case, check the old directory and the warning's path.

## 3. Correlate the evidence

Compare log timestamps with the time you recorded. Read the first relevant exception and its inner exception before following later errors. A failed sign-in, metadata request, or configuration load can cause subsequent symptoms.

For a run failure, use the app's error details and diagnostic export as well as the log; follow [Investigate failures and retry](../app/failures-and-retry.md). Preserve the distinction between rejected writes and writes whose result is uncertain.

Before sharing diagnostics, inspect them for environment URLs, identifiers, generated field values, and exception data that should stay private. Share only the evidence needed to reproduce and diagnose the issue.

## 4. Reproduce under a debugger or test

Open `SeedBomb.slnx`, select `SeedBomb.Wpf` as the startup project, and reproduce under your debugger. In a Debug build the app sets `DOTNET_ENVIRONMENT` to `Development` during startup.

Choose tests based on the failing area:

- Startup and exception handling: WPF tests, including [CrashLogTests](../../../src/SeedBomb.Wpf.Tests/CrashLogTests.cs).
- Settings, connections, profiles, or history: WPF persistence and service tests.
- Generated values or metadata handling: Core tests.
- Batch writes, retry, or relationship handling: Bulk tests and mocked integration tests.

Use [Run focused tests](focused-tests.md) to make the reproduction fast. Add a test that reproduces the observed behavior before fixing it, then repeat both the test and the original app scenario.

The logging implementation is in [FileLoggerProvider](../../../src/SeedBomb.Wpf/Services/Diagnostics/FileLoggerProvider.cs); exception subscriptions and handling are in [App.xaml.cs](../../../src/SeedBomb.Wpf/App.xaml.cs) and [CrashLog](../../../src/SeedBomb.Wpf/Services/Diagnostics/CrashLog.cs).
