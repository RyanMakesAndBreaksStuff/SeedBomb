# Run focused tests

Use this guide to run the tests for a particular change without running every project. Start in the repository root on Windows with .NET 10 and PowerShell 7 available.

## Choose a project

| Change | Start with |
| --- | --- |
| Metadata, graph, generators, or field rules | `src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj` |
| Bulk creation, retry, or relationship processing | `src/SeedBomb.Bulk.Tests/SeedBomb.Bulk.Tests.csproj` |
| View models, app services, persistence, or UI contracts | `src/SeedBomb.Wpf.Tests/SeedBomb.Wpf.Tests.csproj` |
| Core and Bulk working together | `tests/SeedBomb.Integration.Tests/SeedBomb.Integration.Tests.csproj` |

Choose additional projects when the change crosses these boundaries. The WPF tests require the Windows Desktop runtime. Pipeline integration tests use mocked services; passing them does not establish live Dataverse behavior.

## Build before using `--no-build`

After changing source, restore if necessary and build the affected test project:

```powershell
dotnet restore src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj
dotnet build src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-restore -m:1
```

Building the test project also builds its project references. Substitute the project from the table for other areas. Keep build and test commands sequential so you test the newly compiled code.

## Run a class or method

Arguments after `--` go to the xUnit executable runner. Use fully qualified names:

```powershell
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-build --no-restore -- -class SeedBomb.Core.Tests.StringFieldGeneratorTests
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-build --no-restore -- -method SeedBomb.Core.Tests.StringFieldGeneratorTests.Generate_RespectsMaxLength
```

These examples target the current [StringFieldGeneratorTests](../../../src/SeedBomb.Core.Tests/StringFieldGeneratorTests.cs). A theory can produce several test cases even when only one method is selected.

Get the runner's supported arguments with:

```powershell
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj --no-build --no-restore -- -?
```

The help invocation may return a nonzero exit code; it is not a test execution. Use the runner's flags rather than assuming VSTest `--filter` syntax applies.

## Check the result and widen coverage

Read the execution summary. Require zero errors and failures, and a nonzero test total matching your selection. A command that selected no tests has not verified the change.

Run the whole affected project by removing everything after `--no-restore`. Then run neighboring projects if you changed shared contracts or payload generation. For a complete local run, use the four commands in [Build and test SeedBomb locally](../../tutorials/build-and-test.md#4-run-the-executable-test-projects).

If `--no-build` reports a missing executable, build the selected project first. If you built with `-c Release`, also pass `-c Release` to `dotnet run`; otherwise it looks for the Debug output.
