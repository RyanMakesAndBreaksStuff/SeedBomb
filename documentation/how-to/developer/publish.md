# Publish the Windows executable

Use this guide to produce a local self-contained Release executable using the existing publish profiles. Start in the repository root on Windows with the .NET 10 SDK, PowerShell 7, and access to NuGet sources. Complete [Build and test SeedBomb locally](../../tutorials/build-and-test.md) first.

## 1. Publish the required architecture

For x64:

```powershell
dotnet publish src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -c Release -p:PublishProfile=GitHubRelease -o ./publish/x64
```

For x86:

```powershell
dotnet publish src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -c Release -p:PublishProfile=GitHubRelease-x86 -o ./publish/x86
```

The [x64 profile](../../../src/SeedBomb.Wpf/Properties/PublishProfiles/GitHubRelease.pubxml) and [x86 profile](../../../src/SeedBomb.Wpf/Properties/PublishProfiles/GitHubRelease-x86.pubxml) select their runtime identifiers, self-contained deployment, single-file packaging, compression, and embedded debug information. Trimming is disabled. Native libraries are included for extraction at runtime.

Use separate output directories so the two files named `SeedBomb.exe` cannot overwrite one another. This local publish does not create a GitHub Release.

## 2. Check the output

```powershell
Get-Item -LiteralPath ./publish/x64/SeedBomb.exe | Select-Object FullName, Length
```

Substitute `x86` if that is what you published. Require a successful publish and an executable at the expected path. The release workflow also rejects an executable smaller than 1 MB; size alone does not prove that the app works.

Open the executable on a suitable Windows test machine, check the displayed version and startup behavior, and inspect the About page's licenses. A self-contained executable does not require a separately installed .NET runtime, but still needs compatible Windows and any access required for Dataverse sign-in.

## 3. Maintain third-party notices when dependencies change

Ordinary WPF builds validate the existing notice resources automatically. You can repeat that check directly:

```powershell
pwsh -NoProfile -File tools/Generate-ThirdPartyNotices.ps1 -ValidateResources
```

After changing dependencies, first publish x64 Release so the script can inspect the actual published dependency inventory. Regenerate and verify notices:

```powershell
pwsh -NoProfile -File tools/Generate-ThirdPartyNotices.ps1
pwsh -NoProfile -File tools/Generate-ThirdPartyNotices.ps1 -Verify
```

The default input is `src/SeedBomb.Wpf/bin/Release/net10.0-windows10.0.17763.0/win-x64/SeedBomb.deps.json`, with package locations from `src/SeedBomb.Wpf/obj/project.assets.json`. The generator checks for the x64 dependency target. Use its `-DepsPath` and `-AssetsPath` parameters if those inputs are elsewhere.

Generation updates the notice manifest, license copies, index, and inventory. Review the changes. Resolve missing license sources or package overrides through [Generate-ThirdPartyNotices.ps1](../../../tools/Generate-ThirdPartyNotices.ps1) and [ThirdPartyNoticeOverrides.json](../../../tools/ThirdPartyNoticeOverrides.json). `-Verify` compares generated content with the checked-in artifacts without installing new notice files.

Publish again after regeneration so the executable embeds the updated resources, then repeat the output and About-page checks. Run the affected WPF tests, including [ThirdPartyNoticeServiceTests](../../../src/SeedBomb.Wpf.Tests/ThirdPartyNoticeServiceTests.cs).

## 4. Use the existing release workflow when distributing

The [SeedBomb Publish workflow](../../../.github/workflows/seedbomb-publish.yml) accepts a `v*.*.*` tag or a manually supplied version. It runs WPF tests in Release, publishes x64 and x86, validates the executable files, and uploads them to a GitHub Release as `SeedBomb.exe` and `SeedBomb-x86.exe`.

For a local version override, pass `-p:Version=2.0.2` to the publish command, replacing the example with your intended version. The workflow additionally sets assembly, file, and informational versions from its selected version. Check the version shown in the app before distributing an artifact.

Creating or pushing a release tag and dispatching the workflow publishes artifacts to the repository; do that only as part of the intended release process, after reviewing and validating the release contents.
