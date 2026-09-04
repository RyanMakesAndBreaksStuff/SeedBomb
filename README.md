# DataGen

DataGen is a Microsoft Dataverse synthetic-data generator. It reads entity metadata, builds a dependency graph, topologically sorts entities, generates deterministic fake records with [Bogus](https://github.com/bchavez/Bogus), and bulk-creates them in Dataverse using `CreateMultiple` (where supported) or `ExecuteMultiple`.

The same core generation engine is exposed through two front ends:

- **DataGen.Web** — Blazor Server web app with MudBlazor UI and Azure AD / Microsoft.Identity.Web authentication.
- **SeedBomb.Wpf** — Windows WPF desktop app with MSAL multi-profile authentication and DPAPI-encrypted connection profiles.

## Technology stack

- **.NET 10** with C# 14, nullable reference types, and implicit usings.
- **Dataverse SDK**: `Microsoft.PowerPlatform.Dataverse.Client`.
- **Fake data**: `Bogus` with deterministic seeds.
- **Authentication**:
  - Web: `Microsoft.Identity.Web` (Azure AD OIDC).
  - Desktop: `Microsoft.Identity.Client` / MSAL supporting interactive OAuth, client secret (app-only), and certificate (app-only) flows.
- **UI**:
  - Web: `MudBlazor` on Blazor Server.
  - Desktop: `WPF-UI` + `CommunityToolkit.Mvvm`.
- **Testing**: `xunit.v3`, `Moq`, `bunit`, `XrmMockup365`.

## Solution structure

- `DataGen.sln` / `DataGen.slnx` — solution files.
- `Directory.Build.props` — shared MSBuild properties (`net10.0`, warnings as errors, XML documentation files).
- `Directory.Packages.props` — central package management.

### Source projects (`src/`)

| Project | Type | Responsibility |
|---------|------|----------------|
| `DataGen.Core` | Class library | Dataverse metadata provider, dependency graph (`GraphBuilder`, `CycleDetector`, `TopologicalSort`), field generators, edge-case validation, contracts, exceptions. |
| `DataGen.Bulk` | Class library | `BulkCreator`, `GenerationPipeline`, throttle/retry policy, deferred lookup backfill, N:N association handling. References Core. |
| `DataGen.Web` | ASP.NET Core Blazor Server app | DI wiring, scoped Dataverse client factory, lazy metadata/bulk-creator adapters, MudBlazor UI. References Core and Bulk. |
| `SeedBomb.Wpf` | WPF executable (`net10.0-windows10.0.17763.0`) | MVVM view models, connection manager, MSAL auth service, DPAPI profile storage, desktop generation service. Root namespace `Seedbomb`. References Core and Bulk. |

### Test projects

| Project | Location | Notes |
|---------|----------|-------|
| `DataGen.Core.Tests` | `src/DataGen.Core.Tests/` | Unit tests for generators, graph, metadata, validation. |
| `DataGen.Bulk.Tests` | `src/DataGen.Bulk.Tests/` | Unit tests for bulk creation, deferred backfill, throttling. |
| `DataGen.Web.Tests` | `src/DataGen.Web.Tests/` | Tests for Blazor services; uses `bunit`. |
| `DataGen.Wpf.Tests` | `src/DataGen.Wpf.Tests/` | ViewModel tests; requires Windows Desktop runtime. |
| `DataGen.Integration.Tests` | `tests/DataGen.Integration.Tests/` | End-to-end pipeline tests using mocked `IOrganizationServiceAsync2`; `XrmMockup365` is available for full in-memory Dataverse simulation. |

## Build

```bash
# Full solution (Windows)
dotnet build DataGen.sln

# Full solution on non-Windows hosts (required for WPF)
dotnet build DataGen.sln -p:EnableWindowsTargeting=true

# Individual projects
dotnet build src/DataGen.Web/DataGen.Web.csproj
dotnet build src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -p:EnableWindowsTargeting=true
```

## Run

Web app (development):

```bash
dotnet run --project src/DataGen.Web/DataGen.Web.csproj --launch-profile https
# Dev URL: http://localhost:5188
```

WPF app (Windows only):

```bash
dotnet run --project src/SeedBomb.Wpf/SeedBomb.Wpf.csproj
```

## Test

All test projects use **xunit.v3** and are configured as executables (`OutputType=Exe`). The reliable way to run them is:

```bash
dotnet run --project src/DataGen.Core.Tests/DataGen.Core.Tests.csproj
dotnet run --project src/DataGen.Bulk.Tests/DataGen.Bulk.Tests.csproj
dotnet run --project src/DataGen.Web.Tests/DataGen.Web.Tests.csproj
dotnet run --project tests/DataGen.Integration.Tests/DataGen.Integration.Tests.csproj
```

> `DataGen.Wpf.Tests` requires the .NET **Windows Desktop** runtime and must be run on Windows.

`dotnet test DataGen.sln` may fail in some environments because the VSTest host cannot discover the xunit.v3 runner. Use `dotnet run --project <test.csproj>` as the fallback.

> The repo's `global.json` pins the Microsoft.Testing.Platform (MTP) test runner for .NET 10. Without it, `dotnet test` can silently report 0 tests discovered as a "pass" — always confirm the reported test count against the verified counts below.

Verified test counts:

- `DataGen.Core.Tests` — 165 tests
- `DataGen.Bulk.Tests` — 46 tests
- `DataGen.Web.Tests` — 7 tests
- `DataGen.Integration.Tests` — 10 tests

## Configuration and security

- **Web configuration**: `src/DataGen.Web/appsettings.json` contains placeholder Azure AD / Dataverse values. Override `ClientId`, `ClientSecret`, `TenantId`, and `DataverseUrl` via user secrets (`dotnet user-secrets`) or environment variables in production.
- **WPF credentials**: connection profiles are stored in `%LOCALAPPDATA%\DataGen\connections.json`. Client secrets and passwords are encrypted with Windows DPAPI (`DataProtectionScope.CurrentUser`) before being written to disk.
- **Authentication**: the web app enforces authenticated access by default (`RequireAuthenticatedUser` fallback policy). Production uses HTTPS redirection and HSTS.
- **NuGet audit**: a transitive `System.Security.Cryptography.Xml` vulnerability in the Dataverse SDK is acknowledged. `NuGetAuditLevel` is set to `moderate`, and `NU1901`–`NU1903` warnings are not promoted to errors (see `Directory.Build.props`).

## Deploy

- **Web**: publish with `dotnet publish src/DataGen.Web/DataGen.Web.csproj -c Release`. A container launch profile exists in `Properties/launchSettings.json`.
- **WPF**: publish a self-contained single-file exe with `dotnet publish src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -c Release -p:PublishProfile=GitHubRelease -o ./publish`. GitHub Actions workflow **SeedBomb Publish** uploads `SeedBomb.exe` to GitHub Releases (push a `v*.*.*` tag, or run the workflow manually).
- **Secrets** must always be supplied through secure configuration providers; never commit credentials to the repo.

## Project conventions

- Plans and specs live in `plans/` (e.g., `plans/connection.md`).
- Active task tracking is in `tasks/todo.md`; lessons learned are captured in `tasks/lessons.md`.
- Historical documentation is archived in `Docs/olddoc/`.
- See `AGENTS.md` for the full agent guide, code style guidelines, and security notes.
