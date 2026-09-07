# Publish SeedBomb

This repo ships the desktop app as a **self-contained Windows x64 executable**. Recipients do **not** need the .NET SDK or Desktop Runtime installed.

| | |
|---|---|
| Project | `src/SeedBomb.Wpf/SeedBomb.Wpf.csproj` |
| Artifact | `SeedBomb.exe` (~85 MB) |
| Runtime | `win-x64`, self-contained, single-file |
| Workflow | **SeedBomb Publish** (`.github/workflows/seedbomb-publish.yml`) |
| Profile | `src/SeedBomb.Wpf/Properties/PublishProfiles/GitHubRelease.pubxml` |

The exe targets **Windows 10 1809 (build 17763) or later**, 64-bit. It will not run on 32-bit Windows, Windows 7/8, or non-Windows OSes.

---

## 1. Choose a version

Versions must look like `MAJOR.MINOR.PATCH` (optional prerelease suffix):

| Input | Tag created | GitHub release | Prerelease? |
|---|---|---|---|
| `1.0.0` or `v1.0.0` | `v1.0.0` | SeedBomb 1.0.0 | no |
| `1.2.3-beta.1` | `v1.2.3-beta.1` | SeedBomb 1.2.3-beta.1 | yes |

Do **not** reuse a version that already has a GitHub release. `gh release create` fails if that tag/release exists.

---

## 2. Put the workflow on the commit you will ship

The workflow file must exist **on the commit you tag or dispatch from**. Merge `.github/workflows/seedbomb-publish.yml` to the branch you release (usually `main`) before the first publish.

Also required:

- GitHub Actions enabled for the repository
- The default `GITHUB_TOKEN` is enough (`contents: write` is set in the workflow)
- No extra secrets

---

## 3. Publish via GitHub Actions

Pick **one** of the two triggers. Both run tests, publish `SeedBomb.exe`, and attach it to a GitHub Release.

### Option A — Push a version tag (typical)

From the commit you want to ship:

```bash
git checkout main
git pull
git tag v1.0.0
git push origin v1.0.0
```

The tag glob is `v*.*.*` (for example `v1.0.0`, `v1.2.3-beta.1`).

Watch the run: **Actions → SeedBomb Publish**.

### Option B — Run the workflow manually

Use this to ship the **current HEAD of a branch** without creating the tag yourself.

1. Open **Actions → SeedBomb Publish → Run workflow**
2. Choose the branch to build
3. Set **Release version** to `1.0.0` (or `v1.0.0`)
4. Run

The workflow creates tag `v1.0.0` on that commit and the GitHub Release.

---

## 4. What the workflow does

1. Resolves the version from the tag name or the manual input
2. Installs .NET 10
3. Restores and runs `src/DataGen.Wpf.Tests` (`dotnet run --project … -c Release`)
4. Publishes a self-contained single-file exe:

   ```bash
   dotnet publish src/SeedBomb.Wpf/SeedBomb.Wpf.csproj \
     -c Release \
     -r win-x64 \
     --self-contained true \
     -p:PublishProfile=GitHubRelease \
     -p:Version=<version> \
     -o ./publish
   ```

5. Checks that `publish/SeedBomb.exe` exists and is larger than 1 MB
6. Creates a GitHub Release titled `SeedBomb <version>`, generates notes from commits, and uploads `SeedBomb.exe`

If the version contains `-` (for example `1.0.0-rc.1`), the release is marked **prerelease**.

---

## 5. Download and run

1. Open the repo **Releases** page
2. Download `SeedBomb.exe` from the release assets
3. Place it anywhere and double-click

No installer, no extra DLLs, no .NET install. The first launch can take a few extra seconds while native libraries extract under `%TEMP%\.net`. Later launches are faster.

Windows SmartScreen may warn on an unsigned exe (**More info → Run anyway**). This workflow does not code-sign.

---

## 6. Publish locally (no GitHub Release)

Windows machine with the .NET 10 SDK:

```bash
dotnet publish src/SeedBomb.Wpf/SeedBomb.Wpf.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishProfile=GitHubRelease `
  -p:Version=1.0.0 `
  -o ./publish
```

Output: `publish/SeedBomb.exe`

Optional checks before tagging:

```bash
dotnet build src/SeedBomb.Wpf/SeedBomb.Wpf.csproj -c Release
dotnet run --project src/DataGen.Wpf.Tests/DataGen.Wpf.Tests.csproj -c Release
```

In Visual Studio: right-click **SeedBomb.Wpf → Publish** and use the **GitHubRelease** or **FolderProfile** profile (`win-x64`, self-contained, single-file).

---

## 7. Re-publish the same version

GitHub will not overwrite an existing release. To replace `v1.0.0`:

1. Delete the GitHub Release (and the `v1.0.0` tag if you will recreate it)
2. Push the tag again, or re-run **SeedBomb Publish** with the same version

Prefer bumping the patch version (`1.0.1`) instead of rewriting a shipped tag.

---

## 8. Troubleshooting

| Symptom | What to check |
|---|---|
| Workflow missing from Actions | Merge `.github/workflows/seedbomb-publish.yml` to the default branch; enable Actions |
| Tag push did not start a run | Tag must match `v*.*.*` (use `v1.0.0`, not `1.0.0` or `release-1.0.0`) |
| `Version must look like 1.2.3` | Use `1.2.3` or `v1.2.3` (optional `-beta.1` suffix is fine) |
| `release already exists` | That tag already has a release — bump the version or delete the old release |
| Tests fail in CI | Same command as local: `dotnet run --project src/DataGen.Wpf.Tests/DataGen.Wpf.Tests.csproj -c Release` |
| `SeedBomb.exe was not produced` | Inspect the Publish step log; confirm `AssemblyName` is still `SeedBomb` |
| Exe is only a few hundred KB | Self-contained publish failed; the file must be tens of MB |
| Exe will not start on another PC | Needs 64-bit Windows 10 1809+; SmartScreen/AV may quarantine an unsigned exe |
| First start is slow | Expected — single-file extraction to `%TEMP%\.net` |

---

## Related files

- `.github/workflows/seedbomb-publish.yml` — CI release pipeline
- `src/SeedBomb.Wpf/Properties/PublishProfiles/GitHubRelease.pubxml` — self-contained single-file settings
- `src/SeedBomb.Wpf/Properties/PublishProfiles/FolderProfile.pubxml` — local Visual Studio folder publish
- `src/SeedBomb.Wpf/SeedBomb.Wpf.csproj` — `AssemblyName` / `Product` = `SeedBomb`
