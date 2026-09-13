<#
.SYNOPSIS
    Packages the pinned x86_64 Vulkan runtime into an osx-x64 client.
    Intel-only; remove with the rest of osx-x64 support (docs/ci-and-releases.md).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClientDirectory,
    [Parameter(Mandatory)][string]$VulkanRuntimeDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsMacOS) {
    throw 'package-macos-x64-vulkan requires PowerShell 7 on macOS.'
}

$client = [IO.Path]::GetFullPath($ClientDirectory)
if (-not (Test-Path -LiteralPath $client -PathType Container)) {
    throw "Published client directory '$client' does not exist."
}

# Confirms both native executables carry an x86_64 slice before Intel libraries are bundled.
foreach ($name in @('acdream-client', 'acdream-headless')) {
    $executable = Join-Path $client $name
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published client directory '$client' has no $name."
    }
    & /usr/bin/lipo $executable -verify_arch x86_64
    if ($LASTEXITCODE -ne 0) {
        throw "'$executable' has no x86_64 slice; package-macos-x64-vulkan packages Intel clients only."
    }
}

$runtime = [IO.Path]::GetFullPath($VulkanRuntimeDirectory)
if (-not (Test-Path -LiteralPath $runtime -PathType Container)) {
    throw "x86_64 Vulkan runtime directory '$runtime' does not exist."
}

$frameworks = Join-Path $client 'Frameworks'
$clientResources = Join-Path $client 'Resources'
$resources = Join-Path $clientResources 'vulkan'
$icdDirectory = Join-Path $resources 'icd.d'
$licenseDirectory = Join-Path $resources 'licenses'
if (Test-Path -LiteralPath $frameworks) { Remove-Item -LiteralPath $frameworks -Recurse -Force }
if (Test-Path -LiteralPath $resources) { Remove-Item -LiteralPath $resources -Recurse -Force }
New-Item -ItemType Directory -Path $frameworks -Force | Out-Null
New-Item -ItemType Directory -Path $icdDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null

# The client calls mach_absolute_time while pacing frames. Its own privacy
# manifest is required independently of the launcher's app-bundle manifest.
$privacyManifest = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>NSPrivacyAccessedAPITypes</key>
  <array>
    <dict>
      <key>NSPrivacyAccessedAPIType</key>
      <string>NSPrivacyAccessedAPICategorySystemBootTime</string>
      <key>NSPrivacyAccessedAPITypeReasons</key>
      <array>
        <string>35F9.1</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
"@
[IO.File]::WriteAllText(
    (Join-Path $clientResources 'PrivacyInfo.xcprivacy'),
    $privacyManifest,
    [Text.UTF8Encoding]::new($false))

$copied = @{}
foreach ($name in @('libvulkan.1.dylib', 'libMoltenVK.dylib')) {
    $source = Join-Path $runtime "lib/$name"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "x86_64 Vulkan runtime '$runtime' has no 'lib/$name'."
    }
    $destination = Join-Path $frameworks $name
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $copied[$name] = $destination
}

foreach ($destination in $copied.Values) {
    $loadCommands = & /usr/bin/otool -l $destination
    if ($LASTEXITCODE -ne 0) { throw "otool failed for '$destination'." }
    $minos = $loadCommands | Select-String -Pattern '^\s*minos\s+(\S+)' |
        ForEach-Object { [version]$_.Matches[0].Groups[1].Value } |
        Sort-Object -Descending | Select-Object -First 1
    if ($null -eq $minos) { throw "'$destination' has no LC_BUILD_VERSION minos." }
    if ($minos -gt [version]'14.0') {
        throw "'$destination' targets macOS $minos, above the pinned 14.0 deployment target."
    }
}

foreach ($destination in $copied.Values) {
    & /usr/bin/install_name_tool -id ('@loader_path/' + [IO.Path]::GetFileName($destination)) $destination
    if ($LASTEXITCODE -ne 0) { throw "Could not set bundled library identity for '$destination'." }
    $links = & /usr/bin/otool -L $destination
    if ($LASTEXITCODE -ne 0) { throw "otool failed for '$destination'." }
    foreach ($line in $links | Select-Object -Skip 1) {
        $dependency = $line.Trim()
        $index = $dependency.IndexOf(' (', [StringComparison]::Ordinal)
        if ($index -gt 0) { $dependency = $dependency.Substring(0, $index) }
        if ($dependency.StartsWith('/usr/lib/', [StringComparison]::Ordinal) -or
            $dependency.StartsWith('/System/Library/', [StringComparison]::Ordinal) -or
            $dependency.StartsWith('@loader_path/', [StringComparison]::Ordinal)) {
            continue
        }
        throw "Bundled native library '$destination' links to non-system '$dependency'."
    }
}

foreach ($destination in $copied.Values) {
    & /usr/bin/lipo $destination -verify_arch x86_64
    if ($LASTEXITCODE -ne 0) { throw "'$destination' has no x86_64 slice." }
}

foreach ($native in @($copied.Values) + @(
        (Join-Path $client 'acdream-client'),
        (Join-Path $client 'acdream-headless'))) {
    if (-not (Test-Path -LiteralPath $native -PathType Leaf)) {
        throw "Native macOS payload '$native' is missing."
    }
    & /usr/bin/codesign --force --sign - --timestamp=none $native
    if ($LASTEXITCODE -ne 0) { throw "Could not ad-hoc sign '$native'." }
    & /usr/bin/codesign --verify --strict $native
    if ($LASTEXITCODE -ne 0) { throw "Ad-hoc signature validation failed for '$native'." }
}

$icd = @"
{
  "file_format_version": "1.0.0",
  "ICD": {
    "library_path": "../../../Frameworks/libMoltenVK.dylib",
    "api_version": "1.3.0"
  }
}
"@
[IO.File]::WriteAllText((Join-Path $icdDirectory 'MoltenVK_icd.json'), $icd, [Text.UTF8Encoding]::new($false))

foreach ($component in @('molten-vk', 'vulkan-loader')) {
    $license = Join-Path $runtime "licenses/$component-LICENSE"
    if (-not (Test-Path -LiteralPath $license -PathType Leaf)) {
        throw "x86_64 Vulkan runtime '$runtime' has no license text for '$component'."
    }
    Copy-Item -LiteralPath $license -Destination (Join-Path $licenseDirectory "$component-LICENSE") -Force
}

$metadata = [ordered]@{
    schemaVersion = 1
    components = @(Get-Content -LiteralPath (Join-Path $runtime 'components.json') -Raw | ConvertFrom-Json)
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resources 'dependencies.json') -Encoding utf8NoBOM
Write-Host "Bundled $($copied.Count) validated pinned Khronos native library file(s) for osx-x64 Vulkan."
