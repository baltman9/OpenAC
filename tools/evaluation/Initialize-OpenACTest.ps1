[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$ConfigPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $PSScriptRoot 'evaluation.local.json' }

# Machine-specific paths and addresses stay in an ignored local configuration.
$config = & (Join-Path $PSScriptRoot 'Read-OpenACEvaluationConfig.ps1') -ConfigPath $ConfigPath
$sourceRoot = $config.sourceRoot
$testRoot = $config.testRoot
$datRoot = Join-Path $testRoot 'RetailDats'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'retail-dats.json') | ConvertFrom-Json
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-RegularPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -Force -LiteralPath $current
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a linked path: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Assert-Dat([string]$Path, [string]$Hash) {
    Assert-RegularPath $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Hash) {
        throw "Retail DAT mismatch; nothing will be overwritten: $Path"
    }
}

Assert-RegularPath $testRoot
$marker = Join-Path $testRoot 'openac-evaluation-root.txt'
Assert-RegularPath $marker
if (Test-Path -LiteralPath $testRoot) {
    if (-not (Test-Path -LiteralPath $marker) -or
        (Get-Content -Raw -LiteralPath $marker).Trim() -cne 'OpenAC isolated 9010 evaluation v1') {
        throw 'Existing unrecognized OpenAC-Test folder; preserve it for review.'
    }
}
# Verify every source and any existing destination before changing anything.
foreach ($entry in $manifest.PSObject.Properties) {
    Assert-Dat (Join-Path $sourceRoot $entry.Name) $entry.Value
    $destination = Join-Path $datRoot $entry.Name
    if (Test-Path -LiteralPath $destination) { Assert-Dat $destination $entry.Value }
    elseif ($CheckOnly) { throw "Missing test DAT: $destination" }
}
$folders = @('Launcher','RetailDats','PreparedData','Config','Cache','Downloads')
foreach ($folder in $folders) { Assert-RegularPath (Join-Path $testRoot $folder) }
if ($CheckOnly) { Write-Host 'OPENAC-RETAIL-DATS-CHECK-OK'; return }

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $marker)) {
    [IO.File]::WriteAllText($marker, 'OpenAC isolated 9010 evaluation v1', $utf8)
}
foreach ($folder in $folders) {
    New-Item -ItemType Directory -Path (Join-Path $testRoot $folder) -Force | Out-Null
}
foreach ($entry in $manifest.PSObject.Properties) {
    $source = Join-Path $sourceRoot $entry.Name
    $destination = Join-Path $datRoot $entry.Name
    if (-not (Test-Path -LiteralPath $destination)) {
        Copy-Item -LiteralPath $source -Destination $destination
    }
    Assert-Dat $destination $entry.Value
    Assert-Dat $source $entry.Value
}

# Seed only an empty-account local test server; never import existing credentials.
$profilePath = Join-Path $testRoot 'Config\launcher-profiles.json'
Assert-RegularPath $profilePath
if (-not (Test-Path -LiteralPath $profilePath)) {
    $profile = [ordered]@{
        version = 1
        servers = @([ordered]@{name=$config.serverName;host=$config.serverHost;port=9010;accounts=@()})
        users = @()
    }
    [IO.File]::WriteAllText($profilePath, ($profile | ConvertTo-Json -Depth 6), $utf8)
}
Write-Host "OPENAC-TEST-FOLDERS-READY: $testRoot"
Write-Host 'Four Retail DATs copied and hash-verified. Original files and all servers are unchanged.'
