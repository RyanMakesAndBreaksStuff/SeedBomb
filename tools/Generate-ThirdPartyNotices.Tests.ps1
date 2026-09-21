#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$gen = Join-Path $here 'Generate-ThirdPartyNotices.ps1'
if (-not (Test-Path $gen)) { throw 'Generator script not found' }

$root = Join-Path ([IO.Path]::GetTempPath()) ("notice-fixture-" + [guid]::NewGuid().ToString('n'))
$nuget = Join-Path $root 'nuget/demo.pkg/1.0.0'
New-Item -ItemType Directory -Force -Path $nuget, (Join-Path $root 'repo/tools') | Out-Null
$licenseHtml = '<!DOCTYPE html><html><head><title>Fixture license title</title></head><body><h1>DEMO LICENSE</h1><p>Terms</p></body></html>'
Set-Content -LiteralPath (Join-Path $nuget 'LICENSE.html') -Value $licenseHtml -NoNewline
$nuspec = @'
<?xml version="1.0"?>
<package>
  <metadata>
    <id>Demo.Pkg</id>
    <version>1.0.0</version>
    <authors>Demo</authors>
    <license type="file">LICENSE.html</license>
    <projectUrl>https://example.com/demo</projectUrl>
    <copyright>Copyright (c) Demo</copyright>
  </metadata>
</package>
'@
Set-Content -LiteralPath (Join-Path $nuget 'demo.pkg.nuspec') -Value $nuspec
$deps = @{
    runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-x64' }
    targets = @{
        '.NETCoreApp,Version=v10.0' = @{}
        '.NETCoreApp,Version=v10.0/win-x64' = @{
            'Demo.Pkg/1.0.0' = @{ runtime = @{ 'Demo.Pkg.dll' = @{} } }
        }
    }
} | ConvertTo-Json -Depth 8
$depsPath = Join-Path $root 'SeedBomb.deps.json'
Set-Content -LiteralPath $depsPath -Value $deps
$assets = @{ packageFolders = @{ "$(Join-Path $root 'nuget')/" = @{} } } | ConvertTo-Json -Depth 5
$assetsPath = Join-Path $root 'project.assets.json'
Set-Content -LiteralPath $assetsPath -Value $assets

# The real generator resolves repo root as $PSScriptRoot/.. so copy it into the fixture.
Copy-Item $gen (Join-Path $root 'repo/tools/Generate-ThirdPartyNotices.ps1')
$ov = Get-Content (Join-Path $here 'ThirdPartyNoticeOverrides.json') -Raw
# Fixture uses an empty package map; Demo.Pkg has a license file so no override is required.
Set-Content (Join-Path $root 'repo/tools/ThirdPartyNoticeOverrides.json') '{"licenseSources":{},"packages":{},"mitWithoutLicenseFile":[]}'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'repo/tools/notice-sources') | Out-Null

pwsh -NoProfile -File (Join-Path $root 'repo/tools/Generate-ThirdPartyNotices.ps1') -DepsPath $depsPath -AssetsPath $assetsPath
$manifest = Get-Content (Join-Path $root 'repo/src/SeedBomb.Wpf/Resources/ThirdParty/ThirdPartyNotices.json') -Raw | ConvertFrom-Json
if ($manifest.components.Count -ne 1) { throw "expected 1 component, got $($manifest.components.Count)" }
if ($manifest.components[0].name -ne 'Demo.Pkg') { throw 'unexpected component name' }
$lic = Join-Path $root 'repo/licenses/Demo.Pkg-LICENSE.html'
if ((Get-Content $lic -Raw) -ne $licenseHtml) { throw 'license bytes were rewritten' }
$display = Get-Content (Join-Path $root 'repo/licenses/Demo.Pkg-DISPLAY.txt') -Raw
if (-not $display.StartsWith('DEMO LICENSE')) { throw 'display text should start with body content' }
if ($display.Contains('Fixture license title')) { throw 'display text should not include HTML head content' }

pwsh -NoProfile -File (Join-Path $root 'repo/tools/Generate-ThirdPartyNotices.ps1') -DepsPath $depsPath -AssetsPath $assetsPath -Verify
pwsh -NoProfile -File (Join-Path $root 'repo/tools/Generate-ThirdPartyNotices.ps1') -ValidateResources

$jsonPath = Join-Path $root 'repo/src/SeedBomb.Wpf/Resources/ThirdParty/ThirdPartyNotices.json'
(Get-Content $jsonPath -Raw) -replace '1.0.0','9.9.9' | Set-Content $jsonPath
$failed = $false
try { pwsh -NoProfile -File (Join-Path $root 'repo/tools/Generate-ThirdPartyNotices.ps1') -DepsPath $depsPath -AssetsPath $assetsPath -Verify }
catch { $failed = $true }
if ($LASTEXITCODE -ne 0) { $failed = $true }
if (-not $failed) { throw 'Verify should fail after a manifest edit.' }

Remove-Item (Join-Path $root 'repo/src/SeedBomb.Wpf/Resources/ThirdParty/Licenses/Demo.Pkg-DISPLAY.txt')
$failed = $false
try { pwsh -NoProfile -File (Join-Path $root 'repo/tools/Generate-ThirdPartyNotices.ps1') -ValidateResources }
catch { $failed = $true }
if ($LASTEXITCODE -ne 0) { $failed = $true }
if (-not $failed) { throw 'ValidateResources should fail after a missing license.' }

Remove-Item -LiteralPath $root -Recurse -Force
Write-Host 'Fixture tests passed.'
