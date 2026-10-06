# Build and test SeedBomb locally

This tutorial takes you from a local checkout to a compiled Windows app and passing automated tests. You will finish by opening the app. You do not need a Dataverse account to build or run the tests.

## Before you start

Use a Windows computer with the .NET 10 SDK and PowerShell 7 (`pwsh`) installed. The WPF project targets Windows 10 build 17763 or later. Dependency restoration requires access to the configured NuGet sources; the first restore may take several minutes.

Open PowerShell in the repository root, the directory containing `SeedBomb.slnx`. Keep this directory as your working directory throughout the tutorial.

## 1. Check your tools

```powershell
dotnet --list-sdks
pwsh --version
```

Look for a `10.0` SDK and PowerShell `7`. The Windows desktop SDK support comes with the .NET SDK on Windows. PowerShell 7 is also required during WPF builds: the project runs the third-party notice validation script using `pwsh`.

## 2. Find the projects you will build

Open [SeedBomb.slnx](../../SeedBomb.slnx) in your editor. The production projects have three responsibilities:

| Project | What you will build |
| --- | --- |
| `src/SeedBomb.Core` | Metadata, dependencies, field generators, and validation |
| `src/SeedBomb.Bulk` | Generation pipeline, bulk requests, and relationship processing |
| `src/SeedBomb.Wpf` | The Windows desktop app |

The corresponding test projects are under `src`; pipeline integration tests are under `tests/SeedBomb.Integration.Tests`. These integration tests use mocked Dataverse services.

## 3. Restore and build

Run the following commands one at a time:

```powershell
dotnet restore SeedBomb.slnx
dotnet build SeedBomb.slnx --no-restore -m:1
```

Restoration downloads dependencies. The build compiles the projects and validates the app's embedded third-party notices. `-m:1` builds serially, which also makes build failures easier to isolate.

Continue when the build reports success with no errors. Shared build settings treat warnings as errors, so a warning can stop the build. If `pwsh` cannot be found, install PowerShell 7 or correct your `PATH`, open a new terminal, and repeat the build.

If restoration fails, read its first error and resolve the unavailable feed, package, or credential before trying the build again. `--no-restore` assumes the preceding restore succeeded.

## 4. Run the executable test projects

The test projects use xUnit v3 and produce executable runners. Run them with `dotnet run`:

```powershell
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-build --no-restore
dotnet run --project src/SeedBomb.Bulk.Tests/SeedBomb.Bulk.Tests.csproj --no-build --no-restore
dotnet run --project src/SeedBomb.Wpf.Tests/SeedBomb.Wpf.Tests.csproj --no-build --no-restore
dotnet run --project tests/SeedBomb.Integration.Tests/SeedBomb.Integration.Tests.csproj --no-build --no-restore
```

Each runner prints a test execution summary. Check that `Errors` and `Failed` are both zero and that tests actually ran. Inspect any skipped tests before treating the result as coverage for your task. In PowerShell, `$LASTEXITCODE` immediately after a command gives its exit code; zero indicates success.

These tests exercise code and mocked service behavior. They do not verify sign-in, Dataverse permissions, actual server writes, or the appearance of the running desktop app.

## 5. Open the Windows app

```powershell
dotnet run --project src/SeedBomb.Wpf/SeedBomb.Wpf.csproj --no-build --no-restore
```

Wait for the splash screen to finish. On a fresh user profile, the app prompts you to add a connection. With saved connections, it attempts to restore your session; a sign-in prompt may appear instead.

You have now built the source and reached the desktop app. Close the app when finished. To continue with a live environment, follow [Generate your first test records](first-generation.md). To shorten later test runs, follow [Run focused tests](../how-to/developer/focused-tests.md).

## Source checkpoints

The target framework and warning policy are in [Directory.Build.props](../../Directory.Build.props). The WPF target, notice validation, and embedded resources are in [SeedBomb.Wpf.csproj](../../src/SeedBomb.Wpf/SeedBomb.Wpf.csproj). Startup and session restoration are implemented in [App.xaml.cs](../../src/SeedBomb.Wpf/App.xaml.cs).
