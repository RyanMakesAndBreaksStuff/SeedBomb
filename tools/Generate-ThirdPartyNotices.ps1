#Requires -Version 7
[CmdletBinding()]
param(
    [string]$DepsPath,
    [string]$AssetsPath,
    [switch]$Verify,
    [switch]$ValidateResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Utf8 = [System.Text.UTF8Encoding]::new($false)
$OverridesPath = Join-Path $PSScriptRoot 'ThirdPartyNoticeOverrides.json'
$SourcesDir = Join-Path $PSScriptRoot 'notice-sources'
$LicensesDir = Join-Path $RepoRoot 'licenses'
$EmbedDir = Join-Path $RepoRoot 'src/SeedBomb.Wpf/Resources/ThirdParty/Licenses'
$ManifestPath = Join-Path $RepoRoot 'src/SeedBomb.Wpf/Resources/ThirdParty/ThirdPartyNotices.json'
$IndexPath = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'
$InventoryPath = Join-Path $PSScriptRoot 'notice-inventory.json'
$DefaultDeps = Join-Path $RepoRoot 'src/SeedBomb.Wpf/bin/Release/net10.0-windows10.0.17763.0/win-x64/SeedBomb.deps.json'
$DefaultAssets = Join-Path $RepoRoot 'src/SeedBomb.Wpf/obj/project.assets.json'
$RidTarget = '.NETCoreApp,Version=v10.0/win-x64'
$OwnProjects = [System.Collections.Generic.HashSet[string]]::new([string[]]@('SeedBomb', 'DataGen.Core', 'DataGen.Bulk'))

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, $Utf8)
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-SafeRelative([string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative)) { throw 'Empty relative path.' }
    if ($Relative.Contains('..') -or [IO.Path]::IsPathRooted($Relative)) {
        throw "Unsafe path '$Relative'."
    }
}

function Convert-HtmlToPlainText([string]$Html) {
    $t = [regex]::Replace($Html, '(?is)<script[^>]*>.*?</script>', '')
    $t = [regex]::Replace($t, '(?is)<style[^>]*>.*?</style>', '')
    $t = [regex]::Replace($t, '(?i)<br\s*/?>', "`n")
    $t = [regex]::Replace($t, '(?i)</p>', "`n`n")
    $t = [regex]::Replace($t, '(?i)</h[1-6]>', "`n`n")
    $t = [regex]::Replace($t, '(?s)<[^>]+>', '')
    $t = [System.Net.WebUtility]::HtmlDecode($t)
    return ([regex]::Replace($t, "[ \t]+\r?\n", "`n")).Trim()
}

function Convert-DocxToPlainText([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry('word/document.xml')
        if ($null -eq $entry) { throw "DOCX '$Path' has no word/document.xml." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$document = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
        $ns.AddNamespace('w', 'http://schemas.openxmlformats.org/wordprocessingml/2006/main')
        $paragraphs = foreach ($paragraph in $document.SelectNodes('//w:p', $ns)) {
            $parts = foreach ($node in $paragraph.SelectNodes('.//w:t | .//w:tab | .//w:br | .//w:cr', $ns)) {
                switch ($node.LocalName) {
                    't' { $node.InnerText }
                    'tab' { "`t" }
                    default { "`n" }
                }
            }
            ($parts -join '')
        }
        return ($paragraphs -join "`n").Trim()
    }
    finally { $zip.Dispose() }
}

function Convert-RtfToPlainText([string]$Path) {
    Add-Type -AssemblyName PresentationFramework
    $stream = [IO.File]::OpenRead($Path)
    try {
        $document = [Windows.Documents.FlowDocument]::new()
        $range = [Windows.Documents.TextRange]::new($document.ContentStart, $document.ContentEnd)
        $range.Load($stream, [Windows.DataFormats]::Rtf)
        return $range.Text.Trim()
    }
    finally { $stream.Dispose() }
}

function Get-NoteProperty($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Get-Overrides {
    $raw = Get-Content -LiteralPath $OverridesPath -Raw -Encoding utf8
    $json = $raw | ConvertFrom-Json
    foreach ($prop in $json.licenseSources.PSObject.Properties) {
        $src = $prop.Value
        $path = Join-Path $SourcesDir $src.file
        Assert-SafeRelative $src.file
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing license source $($src.file)." }
        $hash = Get-Sha256 $path
        if ($hash -ne $src.sha256) { throw "Hash mismatch for $($src.file): $hash" }
    }
    return $json
}

function Get-PackageOverride($Overrides, [string]$Key) {
    $pkg = Get-NoteProperty $Overrides.packages $Key
    $group = $Overrides.mitWithoutLicenseFile
    if ($null -eq $pkg) {
        if ($group -contains $Key) {
            return [pscustomobject]@{ licenseSource = 'dotnet-mit' }
        }
        return $null
    }
    if (($null -eq (Get-NoteProperty $pkg 'licenseSource')) -and ($group -contains $Key)) {
        $pkg | Add-Member -NotePropertyName licenseSource -NotePropertyValue 'dotnet-mit' -Force
    }
    return $pkg
}

function Get-NugetRoots([string]$Assets) {
    $roots = [System.Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $Assets) {
        $json = Get-Content -LiteralPath $Assets -Raw | ConvertFrom-Json
        foreach ($prop in $json.packageFolders.PSObject.Properties) {
            $roots.Add($prop.Name.TrimEnd('\', '/'))
        }
    }
    $fallback = Join-Path $env:USERPROFILE '.nuget/packages'
    if ((Test-Path $fallback) -and -not ($roots | Where-Object { $_ -eq $fallback.TrimEnd('\', '/') })) {
        $roots.Add($fallback)
    }
    if ($roots.Count -eq 0) { throw 'No NuGet package folders found.' }
    return $roots
}

function Resolve-PackageDirectory([string]$PackageId, [string]$Version, $Roots) {
    $folder = $PackageId.ToLowerInvariant()
    foreach ($root in $Roots) {
        $dir = Join-Path $root (Join-Path $folder $Version)
        if (Test-Path -LiteralPath $dir) { return $dir }
    }
    throw "Package directory not found for $PackageId/$Version."
}

function Get-Nuspec([string]$Dir, [string]$PackageId) {
    $nuspec = Get-ChildItem -LiteralPath $Dir -Filter '*.nuspec' | Select-Object -First 1
    if ($null -eq $nuspec) { throw "No nuspec in $Dir." }
    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName
    return $xml.package.metadata
}

function Find-RootLicenseFile([string]$Dir) {
    $names = @('LICENSE', 'LICENSE.txt', 'LICENSE.TXT', 'License.md', 'LICENSE.md')
    foreach ($name in $names) {
        $path = Join-Path $Dir $name
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return $null
}

function Find-RootNoticeFiles([string]$Dir) {
    Get-ChildItem -LiteralPath $Dir -File | Where-Object {
        $_.Name -match '^(ThirdPartyNotices\.txt|THIRD-PARTY-NOTICES\.TXT)$'
    }
}

function Get-PublishedLibraries([string]$Path) {
    $deps = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $targetName = $deps.runtimeTarget.name
    if ($targetName -ne $RidTarget) {
        throw "Expected runtimeTarget '$RidTarget' but found '$targetName'."
    }
    $target = Get-NoteProperty $deps.targets $targetName
    if ($null -eq $target) { throw "deps.json has no target '$targetName'." }
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($prop in $target.PSObject.Properties) {
        $id, $version = $prop.Name.Split('/', 2)
        $lib = $prop.Value
        $type = 'package'
        if ($id.StartsWith('runtimepack.')) {
            $type = 'runtimepack'
            $id = $id.Substring('runtimepack.'.Length)
        }
        if ($OwnProjects.Contains($id)) { continue }
        if ($type -notin @('package', 'runtimepack')) { throw "Unknown library type for $($prop.Name)." }
        $hasAsset = $false
        foreach ($kind in @('runtime', 'native', 'resources', 'runtimeTargets')) {
            if ($null -ne (Get-NoteProperty $lib $kind)) { $hasAsset = $true }
        }
        if (-not $hasAsset) { continue }
        $list.Add([pscustomobject]@{ Id = $id; Version = $version; Key = "$id/$version" })
    }
    if ($list.Count -eq 0) { throw 'Published inventory is empty.' }
    return $list | Sort-Object Id, Version
}

function Get-SourceFile($Overrides, [string]$SourceId) {
    $src = $Overrides.licenseSources.$SourceId
    if ($null -eq $src) { throw "Unknown license source '$SourceId'." }
    return Join-Path $SourcesDir $src.file
}

function New-DisplayText([string[]]$Blocks) {
    return (($Blocks | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join "`n`n").Trim() + "`n"
}

function Publish-File([string]$Source, [string]$FileName, [string]$Staging) {
    $dest = Join-Path $Staging $FileName
    [IO.File]::Copy($Source, $dest, $true)
    return $FileName
}

function Build-Inventory {
    param($Overrides, [string]$Deps, [string]$Assets, [string]$Staging)
    $roots = Get-NugetRoots $Assets
    $libs = Get-PublishedLibraries $Deps
    $components = [System.Collections.Generic.List[object]]::new()
    $generated = [System.Collections.Generic.List[string]]::new()

    foreach ($lib in $libs) {
        $dir = Resolve-PackageDirectory $lib.Id $lib.Version $roots
        $meta = Get-Nuspec $dir $lib.Id
        $ov = Get-PackageOverride $Overrides $lib.Key
        $licenseId = Get-NoteProperty $ov 'license'
        if ([string]::IsNullOrWhiteSpace($licenseId)) {
            if ($meta.license -is [System.Xml.XmlElement]) { $licenseId = [string]$meta.license.InnerText }
            elseif ($meta.license) { $licenseId = [string]$meta.license }
        }
        $copyright = Get-NoteProperty $ov 'copyright'
        if ([string]::IsNullOrWhiteSpace($copyright)) { $copyright = [string]$meta.copyright }
        $projectUrl = Get-NoteProperty $ov 'projectUrl'
        if ([string]::IsNullOrWhiteSpace($projectUrl)) {
            $projectUrl = [string]$meta.projectUrl
            if ([string]::IsNullOrWhiteSpace($projectUrl)) { $projectUrl = [string]$meta.repository.url }
        }
        if ([string]::IsNullOrWhiteSpace($projectUrl) -or $projectUrl -notmatch '^https://') {
            throw "$($lib.Key) is missing an https projectUrl."
        }

        $licensePath = $null
        $noticePaths = @()
        $extraTexts = [System.Collections.Generic.List[string]]::new()
        $originalNames = [System.Collections.Generic.List[string]]::new()

        $licenseSource = Get-NoteProperty $ov 'licenseSource'
        if ($licenseSource) {
            $licensePath = Get-SourceFile $Overrides $licenseSource
        }
        elseif ($meta.license.type -eq 'file' -and $meta.license.InnerText) {
            $candidate = Join-Path $dir $meta.license.InnerText
            if (-not (Test-Path -LiteralPath $candidate)) { throw "$($lib.Key) license file missing: $candidate" }
            $licensePath = $candidate
        }
        else {
            $licensePath = Find-RootLicenseFile $dir
        }

        if (-not $licensePath) { throw "$($lib.Key) has no license text. Add an override." }
        if ([string]::IsNullOrWhiteSpace($licenseId)) { throw "$($lib.Key) has no license identifier." }

        $ext = [IO.Path]::GetExtension($licensePath)
        if ([string]::IsNullOrWhiteSpace($ext)) { $ext = '.txt' }
        $licenseName = "$($lib.Id)-LICENSE$ext"
        [void](Publish-File $licensePath $licenseName $Staging)
        $generated.Add($licenseName)
        $originalNames.Add($licenseName)

        $licenseText = switch ([IO.Path]::GetExtension($licensePath).ToLowerInvariant()) {
            '.html' { Convert-HtmlToPlainText ([IO.File]::ReadAllText($licensePath)) }
            '.rtf' { Convert-RtfToPlainText $licensePath }
            default { [IO.File]::ReadAllText($licensePath) }
        }
        $extraTexts.Add($licenseText)

        foreach ($notice in (Find-RootNoticeFiles $dir)) {
            $noticeName = "$($lib.Id)-NOTICES$($notice.Extension)"
            [void](Publish-File $notice.FullName $noticeName $Staging)
            $generated.Add($noticeName)
            $originalNames.Add($noticeName)
            $extraTexts.Add([IO.File]::ReadAllText($notice.FullName))
        }

        if ($lib.Id -eq 'Microsoft.PowerPlatform.Dataverse.Client') {
            $docx = Join-Path $dir 'lib/net462/Third Party Notices for Dynamics 365 SDK.docx'
            if (-not (Test-Path -LiteralPath $docx)) { throw "Missing Dataverse third-party notices DOCX." }
            $docxName = "$($lib.Id)-NOTICES.docx"
            [void](Publish-File $docx $docxName $Staging)
            $generated.Add($docxName)
            $originalNames.Add($docxName)
            $extraTexts.Add((Convert-DocxToPlainText $docx))
        }

        $displayName = "$($lib.Id)-DISPLAY.txt"
        $needsDisplay = ($extraTexts.Count -gt 1) -or ([IO.Path]::GetExtension($licensePath).ToLowerInvariant() -in @('.html', '.rtf', '.docx'))
        $resourceFile = $licenseName
        if ($needsDisplay) {
            Write-Utf8 (Join-Path $Staging $displayName) (New-DisplayText $extraTexts)
            $generated.Add($displayName)
            $resourceFile = $displayName
        }

        $purpose = Get-NoteProperty $ov 'purpose'
        if ($null -eq $purpose) { $purpose = '' }
        $credit = Get-NoteProperty $ov 'credit'
        $featured = [bool](Get-NoteProperty $ov 'featured')
        $featuredOrder = 0
        $featuredOrderValue = Get-NoteProperty $ov 'featuredOrder'
        if ($featured -and $featuredOrderValue) { $featuredOrder = [int]$featuredOrderValue }

        $components.Add([ordered]@{
            name            = $lib.Id
            version         = $lib.Version
            purpose         = [string]$purpose
            license         = [string]$licenseId
            copyright       = [string]$copyright
            projectUrl      = [string]$projectUrl
            licenseResource = "Seedbomb.Licenses.$resourceFile"
            featured        = $featured
            credit          = $(if ($credit) { [string]$credit } else { $null })
            featuredOrder   = $(if ($featured) { $featuredOrder } else { $null })
        })
    }

    return [pscustomobject]@{
        Components = $components
        Generated  = $generated
    }
}

function Write-Index($Components) {
    $lines = [System.Collections.Generic.List[string]]::new()
    [void]$lines.Add('# Third-party notices')
    [void]$lines.Add('')
    [void]$lines.Add('This file lists copyright and license information for software distributed with SeedBomb.')
    [void]$lines.Add('Full license texts are in [`licenses/`](licenses/).')
    [void]$lines.Add('')
    foreach ($c in $Components) {
        $file = $c.licenseResource.Substring('Seedbomb.Licenses.'.Length)
        [void]$lines.Add("## $($c.name) $($c.version)")
        [void]$lines.Add('')
        [void]$lines.Add("- License: $($c.license)")
        if ($c.copyright) { [void]$lines.Add("- Copyright: $($c.copyright)") }
        [void]$lines.Add("- Project: <$($c.projectUrl)>")
        [void]$lines.Add("- License text: [`licenses/$file`](licenses/$file)")
        [void]$lines.Add('')
    }
    return ($lines -join "`n")
}

function Write-ManifestJson($Components) {
    $payload = [ordered]@{ components = @() }
    foreach ($c in $Components) {
        $item = [ordered]@{
            name            = $c.name
            version         = $c.version
            purpose         = $c.purpose
            license         = $c.license
            copyright       = $c.copyright
            projectUrl      = $c.projectUrl
            licenseResource = $c.licenseResource
            featured        = [bool]$c.featured
        }
        if ($c.credit) { $item.credit = $c.credit }
        if ($null -ne $c.featuredOrder) { $item.featuredOrder = $c.featuredOrder }
        $payload.components += $item
    }
    $opts = [System.Text.Json.JsonSerializerOptions]::new()
    $opts.WriteIndented = $true
    $opts.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    return [System.Text.Json.JsonSerializer]::Serialize($payload, $opts) + "`n"
}

function Install-Tree([string]$Staging, $Generated) {
    New-Item -ItemType Directory -Force -Path $LicensesDir, $EmbedDir, (Split-Path $ManifestPath) | Out-Null
    $previous = @()
    if (Test-Path $InventoryPath) {
        $previous = @(Get-Content $InventoryPath -Raw | ConvertFrom-Json | Select-Object -ExpandProperty files)
    }
    foreach ($name in $Generated) {
        Copy-Item -LiteralPath (Join-Path $Staging $name) -Destination (Join-Path $LicensesDir $name) -Force
        Copy-Item -LiteralPath (Join-Path $Staging $name) -Destination (Join-Path $EmbedDir $name) -Force
    }
    foreach ($name in $previous) {
        if ($Generated -notcontains $name) {
            foreach ($dir in @($LicensesDir, $EmbedDir)) {
                $path = Join-Path $dir $name
                if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
            }
        }
    }
}

function Invoke-ValidateResources {
    if (-not (Test-Path $ManifestPath)) { throw "Missing $ManifestPath" }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $errors = [System.Collections.Generic.List[string]]::new()
    $keys = [System.Collections.Generic.HashSet[string]]::new()
    $i = 0
    foreach ($c in @($manifest.components)) {
        $i++
        foreach ($field in @('name', 'version', 'license', 'projectUrl', 'licenseResource')) {
            if ([string]::IsNullOrWhiteSpace([string]$c.$field)) {
                $errors.Add("Component $i missing $field")
            }
        }
        if ($c.projectUrl -and $c.projectUrl -notmatch '^https://') {
            $errors.Add("$($c.name) projectUrl is not https")
        }
        $key = "$($c.name)/$($c.version)"
        if (-not $keys.Add($key)) { $errors.Add("Duplicate $key") }
        if ($c.licenseResource -notmatch '^Seedbomb\.Licenses\.[A-Za-z0-9._-]+$') {
            $errors.Add("$($c.name) has unsafe licenseResource '$($c.licenseResource)'")
            continue
        }
        $file = $c.licenseResource.Substring('Seedbomb.Licenses.'.Length)
        foreach ($dir in @($LicensesDir, $EmbedDir)) {
            if (-not (Test-Path -LiteralPath (Join-Path $dir $file))) {
                $errors.Add("Missing $file in $dir")
            }
        }
    }
    if ($errors.Count) {
        $errors | ForEach-Object { Write-Error $_ }
        throw "Resource validation failed with $($errors.Count) error(s)."
    }
    Write-Host "Validated $($manifest.components.Count) third-party components."
}

if ($ValidateResources) {
    Invoke-ValidateResources
    return
}

if (-not $DepsPath) { $DepsPath = $DefaultDeps }
if (-not $AssetsPath) { $AssetsPath = $DefaultAssets }
if (-not (Test-Path -LiteralPath $DepsPath)) {
    throw "deps.json not found at $DepsPath. Publish SeedBomb win-x64 Release first."
}

$overrides = Get-Overrides
$staging = Join-Path ([IO.Path]::GetTempPath()) ("seedbomb-notices-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $built = Build-Inventory -Overrides $overrides -Deps $DepsPath -Assets $AssetsPath -Staging $staging
    $manifestText = Write-ManifestJson $built.Components
    $indexText = Write-Index $built.Components
    Write-Utf8 (Join-Path $staging 'ThirdPartyNotices.json') $manifestText
    Write-Utf8 (Join-Path $staging 'THIRD-PARTY-NOTICES.md') $indexText

    if ($Verify) {
        $mismatch = [System.Collections.Generic.List[string]]::new()
        if ((Get-Content -LiteralPath $ManifestPath -Raw) -ne $manifestText) { $mismatch.Add($ManifestPath) }
        if ((Get-Content -LiteralPath $IndexPath -Raw) -ne $indexText) { $mismatch.Add($IndexPath) }
        foreach ($name in $built.Generated) {
            foreach ($dir in @($LicensesDir, $EmbedDir)) {
                $expected = Join-Path $staging $name
                $actual = Join-Path $dir $name
                if (-not (Test-Path $actual)) { $mismatch.Add($actual); continue }
                if ((Get-Sha256 $expected) -ne (Get-Sha256 $actual)) { $mismatch.Add($actual) }
            }
        }
        if ($mismatch.Count) {
            throw "Notice verification failed:`n$($mismatch -join "`n")"
        }
        $featured = @($built.Components | Where-Object featured).Count
        Write-Host "Verified $($built.Components.Count) components ($featured featured). No files written."
        return
    }

    Install-Tree $staging $built.Generated
    Write-Utf8 $ManifestPath $manifestText
    Write-Utf8 $IndexPath $indexText
    Write-Utf8 $InventoryPath ((ConvertTo-Json @{ files = @($built.Generated) } -Depth 5) + "`n")
    $featured = @($built.Components | Where-Object featured).Count
    Write-Host "Wrote $($built.Components.Count) components ($featured featured)."
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
