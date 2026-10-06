# SeedBomb

SeedBomb is a Microsoft Dataverse synthetic-data generator. It reads entity metadata, builds a dependency graph, topologically sorts entities, generates deterministic fake records with [Bogus](https://github.com/bchavez/Bogus), and bulk-creates them in Dataverse using `CreateMultiple` (where supported) or `ExecuteMultiple`.

It ships as **SeedBomb.Wpf**, a Windows WPF desktop app with MSAL multi-profile authentication and DPAPI-encrypted connection profiles.

Start with the [tutorials and how-to guides](documentation/index.md) for using the app or building and extending SeedBomb.

## Documentation

- **Tutorials**: [Generate your first test records](documentation/tutorials/first-generation.md) · [Build and test SeedBomb locally](documentation/tutorials/build-and-test.md)
- **App how-to guides**: [Connections](documentation/how-to/app/connections.md) · [Tables and counts](documentation/how-to/app/tables-and-counts.md) · [Field rules](documentation/how-to/app/field-rules.md) · [Generation profiles](documentation/how-to/app/generation-profiles.md) · [Review and run](documentation/how-to/app/review-and-run.md) · [Failures and retry](documentation/how-to/app/failures-and-retry.md) · [History](documentation/how-to/app/history.md)
- **Developer how-to guides**: [Focused tests](documentation/how-to/developer/focused-tests.md) · [Field generators](documentation/how-to/developer/field-generator.md) · [Diagnostics](documentation/how-to/developer/diagnostics.md) · [Publish](documentation/how-to/developer/publish.md)
- **Verification record**: [What was checked when the guides were written](documentation/VERIFICATION.md)

## Legacy Blazor version

SeedBomb previously also shipped **SeedBomb.Web**, a Blazor Server app (MudBlazor UI, Microsoft.Identity.Web auth) on the same Core/Bulk engine. It was removed from `master` and is no longer maintained. The last version is preserved on the [`blazor`](https://github.com/RyanMakesAndBreaksStuff/SeedBomb/tree/blazor) branch.

## Technology stack

- **.NET 10** with C# 14, nullable reference types, and implicit usings.
- **Dataverse SDK**: `Microsoft.PowerPlatform.Dataverse.Client`.
- **Fake data**: `Bogus` with deterministic seeds.
- **Authentication**: `Microsoft.Identity.Client` / MSAL supporting interactive OAuth, client secret (app-only), and certificate (app-only) flows.
- **UI**: `WPF-UI` + `CommunityToolkit.Mvvm`.
- **Testing**: `xunit.v3`, `Moq`, `XrmMockup365`.

## Solution structure

- `SeedBomb.slnx` — solution file.
- `Directory.Build.props` — shared MSBuild properties (`net10.0`, warnings as errors, XML documentation files).
- `Directory.Packages.props` — central package management.

### Source projects (`src/`)

| Project | Type | Responsibility |
|---------|------|----------------|
| `SeedBomb.Core` | Class library | Dataverse metadata provider, dependency graph (`GraphBuilder`, `CycleDetector`, `TopologicalSort`), field generators, edge-case validation, contracts, exceptions. |
| `SeedBomb.Bulk` | Class library | `BulkCreator`, `GenerationPipeline`, throttle/retry policy, deferred lookup backfill, N:N association handling. References Core. |
| `SeedBomb.Wpf` | WPF executable (`net10.0-windows10.0.17763.0`) | MVVM view models, connection manager, MSAL auth service, DPAPI profile storage, desktop generation service. Root namespace `SeedBomb`. References Core and Bulk. |

### Test projects

| Project | Location | Notes |
|---------|----------|-------|
| `SeedBomb.Core.Tests` | `src/SeedBomb.Core.Tests/` | Unit tests for generators, graph, metadata, validation. |
| `SeedBomb.Bulk.Tests` | `src/SeedBomb.Bulk.Tests/` | Unit tests for bulk creation, deferred backfill, throttling. |
| `SeedBomb.Wpf.Tests` | `src/SeedBomb.Wpf.Tests/` | ViewModel tests; requires Windows Desktop runtime. |
| `SeedBomb.Integration.Tests` | `tests/SeedBomb.Integration.Tests/` | End-to-end pipeline tests using mocked `IOrganizationServiceAsync2`; `XrmMockup365` is available for full in-memory Dataverse simulation. |

## Build

```bash
# Full solution (Windows)
dotnet build SeedBomb.slnx

# Full solution on non-Windows hosts (required for WPF)
dotnet build SeedBomb.slnx -p:EnableWindowsTargeting=true

# Individual projects
dotnet build src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -p:EnableWindowsTargeting=true
```

## Run

WPF app (Windows only):

```bash
dotnet run --project src/SeedBomb.Wpf/SeedBomb.Wpf.csproj
```

## Test

All test projects use **xunit.v3** and are configured as executables (`OutputType=Exe`). The reliable way to run them is:

```bash
dotnet run --project src/SeedBomb.Core.Tests/SeedBomb.Core.Tests.csproj
dotnet run --project src/SeedBomb.Bulk.Tests/SeedBomb.Bulk.Tests.csproj
dotnet run --project tests/SeedBomb.Integration.Tests/SeedBomb.Integration.Tests.csproj
```

> `SeedBomb.Wpf.Tests` requires the .NET **Windows Desktop** runtime and must be run on Windows.

`dotnet test SeedBomb.slnx` may fail in some environments because the VSTest host cannot discover the xunit.v3 runner. Use `dotnet run --project <test.csproj>` as the fallback.

> The repo's `global.json` pins the Microsoft.Testing.Platform (MTP) test runner for .NET 10. Without it, `dotnet test` can silently report 0 tests discovered as a "pass" — always confirm the reported test count against the verified counts below.

Verified test counts:

- `SeedBomb.Core.Tests` — 165 tests
- `SeedBomb.Bulk.Tests` — 46 tests
- `SeedBomb.Integration.Tests` — 10 tests

## Configuration and security

- **WPF credentials**: connection profiles are stored in `%LOCALAPPDATA%\SeedBomb\connections.json`. Client secrets and passwords are encrypted with Windows DPAPI (`DataProtectionScope.CurrentUser`) before being written to disk.
- **NuGet audit**: `NuGetAuditLevel` is `moderate` and advisories fail the build (`TreatWarningsAsErrors`). The Dataverse SDK's transitive `System.Security.Cryptography.Xml` is pinned to a patched stable version for `net10.0` projects in `Directory.Packages.props`; the WPF app uses the copy in the Windows Desktop framework.

## Deploy

- **WPF**: publish a self-contained single-file exe with `dotnet publish src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -c Release -p:PublishProfile=GitHubRelease -o ./publish` (use `GitHubRelease-x86` for 32-bit). GitHub Actions workflow **SeedBomb Publish** uploads `SeedBomb.exe` (x64) and `SeedBomb-x86.exe` (x86) to GitHub Releases (push a `v*.*.*` tag, or run the workflow manually).
- **Secrets** must always be supplied through secure configuration providers; never commit credentials to the repo.

## License

SeedBomb is licensed under the [BSD 3-Clause License](LICENSE) (`BSD-3-Clause`). See [LICENSE](LICENSE).


