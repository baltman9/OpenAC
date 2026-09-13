<#
.SYNOPSIS
    Builds the pinned x86_64 Vulkan runtime bundled into osx-x64 clients (docs/ci-and-releases.md).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -lt 7 -or -not $IsMacOS) {
    throw 'build-macos-x64-vulkan requires PowerShell 7 on macOS.'
}

$pins = [ordered]@{
    moltenVkVersion = '1.4.2'
    moltenVkUrl = 'https://github.com/KhronosGroup/MoltenVK/releases/download/v1.4.2/MoltenVK-macos.tar'
    moltenVkSha256 = 'f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e'
    loaderVersion = '1.4.357.0'
    loaderRepository = 'https://github.com/KhronosGroup/Vulkan-Loader.git'
    loaderTag = 'vulkan-sdk-1.4.357.0'
    loaderCommit = '5f157b62e333c63260d05d81bf66faa216ab0fb8'
    macosDeploymentTarget = '14.0'
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
$stampPath = Join-Path $output 'pins.json'
$stamp = ($pins | ConvertTo-Json).Trim()
$libraries = @('lib/libvulkan.1.dylib', 'lib/libMoltenVK.dylib')
if ((Test-Path -LiteralPath $stampPath -PathType Leaf) -and
    (Get-Content -LiteralPath $stampPath -Raw).Trim() -ceq $stamp -and
    @($libraries | Where-Object { -not (Test-Path -LiteralPath (Join-Path $output $_) -PathType Leaf) }).Count -eq 0) {
    Write-Host "Reusing the pinned x86_64 Vulkan runtime in '$output'."
    return
}

foreach ($tool in @('cmake', 'git', 'python3')) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "build-macos-x64-vulkan needs '$tool' on PATH."
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('openac-x64-vulkan-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $archive = Join-Path $work 'MoltenVK-macos.tar'
    Invoke-WebRequest -Uri $pins.moltenVkUrl -OutFile $archive -MaximumRetryCount 3 -RetryIntervalSec 5
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $pins.moltenVkSha256) {
        throw "MoltenVK archive SHA-256 '$actual' does not match the pinned $($pins.moltenVkSha256)."
    }
    & /usr/bin/tar -xf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the MoltenVK archive.' }
    $moltenVkRoot = Join-Path $work 'MoltenVK'
    $moltenVk = Join-Path $moltenVkRoot 'MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib'
    if (-not (Test-Path -LiteralPath $moltenVk -PathType Leaf)) {
        throw "The MoltenVK archive has no '$moltenVk'."
    }

    $loaderSource = Join-Path $work 'Vulkan-Loader'
    & git clone --quiet --depth 1 --branch $pins.loaderTag $pins.loaderRepository $loaderSource
    if ($LASTEXITCODE -ne 0) { throw "Could not clone Vulkan-Loader at $($pins.loaderTag)." }
    $commit = (& git -C $loaderSource rev-parse HEAD).Trim()
    if ($commit -cne $pins.loaderCommit) {
        throw "Vulkan-Loader tag $($pins.loaderTag) resolved to $commit, not the pinned $($pins.loaderCommit)."
    }

    # UPDATE_DEPS fetches Vulkan-Headers at the revision this loader tag records.
    $loaderBuild = Join-Path $work 'loader-build'
    & cmake -S $loaderSource -B $loaderBuild -D UPDATE_DEPS=ON -D BUILD_TESTS=OFF `
        -D CMAKE_BUILD_TYPE=Release -D CMAKE_OSX_ARCHITECTURES=x86_64 `
        -D CMAKE_OSX_DEPLOYMENT_TARGET=$($pins.macosDeploymentTarget)
    if ($LASTEXITCODE -ne 0) { throw 'Vulkan-Loader configuration failed.' }
    & cmake --build $loaderBuild --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw 'Vulkan-Loader build failed.' }
    $loader = Get-ChildItem -LiteralPath $loaderBuild -Recurse -Filter 'libvulkan.1.dylib' | Select-Object -First 1
    if ($null -eq $loader) { throw 'The Vulkan-Loader build produced no libvulkan.1.dylib.' }
    $loaderTarget = $loader.ResolveLinkTarget($true)
    $loaderPath = if ($null -eq $loaderTarget) { $loader.FullName } else { $loaderTarget.FullName }

    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $output 'lib') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
    Copy-Item -LiteralPath $loaderPath -Destination (Join-Path $output 'lib/libvulkan.1.dylib')
    & /usr/bin/lipo $moltenVk -thin x86_64 -output (Join-Path $output 'lib/libMoltenVK.dylib')
    if ($LASTEXITCODE -ne 0) { throw "Could not thin '$moltenVk' to x86_64." }
    Copy-Item -LiteralPath (Join-Path $loaderSource 'LICENSE.txt') -Destination (Join-Path $output 'licenses/vulkan-loader-LICENSE')
    Copy-Item -LiteralPath (Join-Path $moltenVkRoot 'LICENSE') -Destination (Join-Path $output 'licenses/molten-vk-LICENSE')

    foreach ($library in $libraries) {
        $path = Join-Path $output $library
        & /usr/bin/lipo $path -verify_arch x86_64
        if ($LASTEXITCODE -ne 0) { throw "'$path' has no x86_64 slice." }
    }

    $components = @(
        [ordered]@{
            name = 'molten-vk'
            version = $pins.moltenVkVersion
            source = [ordered]@{
                url = $pins.moltenVkUrl
                sha256 = $pins.moltenVkSha256
                architectures = 'x86_64'
            }
            license = 'Apache-2.0'
        },
        [ordered]@{
            name = 'vulkan-loader'
            version = $pins.loaderVersion
            source = [ordered]@{
                repository = $pins.loaderRepository
                tag = $pins.loaderTag
                commit = $pins.loaderCommit
                architectures = 'x86_64'
            }
            license = 'Apache-2.0'
        })
    ConvertTo-Json -InputObject $components -Depth 4 |
        Set-Content -LiteralPath (Join-Path $output 'components.json') -Encoding utf8NoBOM
    Set-Content -LiteralPath $stampPath -Value $stamp -Encoding utf8NoBOM
    Write-Host "Built the pinned x86_64 Vulkan runtime in '$output'."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
