[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$ConfigPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $PSScriptRoot 'evaluation.local.json' }
$config = & (Join-Path $PSScriptRoot 'Read-OpenACEvaluationConfig.ps1') -ConfigPath $ConfigPath
$root = $config.testRoot
& (Join-Path $PSScriptRoot 'Initialize-OpenACTest.ps1') -CheckOnly -ConfigPath $ConfigPath
$launcher = Join-Path $root 'Launcher\acdream-launcher.exe'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Stage the official OpenAC Windows launcher first.' }
# Detect link redirection of the executable as well as the roots checked above.
if ((Get-Item -Force -LiteralPath $launcher).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The launcher must not be a linked file.'
}
$profilePath = Join-Path $root 'Config\launcher-profiles.json'
if ((Get-Item -Force -LiteralPath $profilePath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The launcher profile must not be a linked file.'
}
$profile = Get-Content -Raw -LiteralPath $profilePath | ConvertFrom-Json
if ([int]$profile.version -ne 1 -or @($profile.servers).Count -ne 1 -or
    [string]$profile.servers[0].host -cne $config.serverHost -or [int]$profile.servers[0].port -ne 9010) {
    throw 'This wrapper requires only the isolated ACE host from local configuration, on port 9010.'
}
Write-Host "Launcher: $launcher"
Write-Host "Retail DAT folder: $root\RetailDats"
Write-Host ("Server: {0} -> {1}:9010" -f $config.serverName, $config.serverHost)
Write-Host 'Choose the RetailDats folder in the launcher. Do not import Thwarg profiles or enable plugins.'
Write-Host 'Use a dedicated test password; launcher profile files can contain credentials.'
Write-Host 'Upstream launcher/client updates are allowed. Record the actual version before each test.'
if ($CheckOnly) { Write-Host 'OPENAC-LAUNCHER-CHECK-OK (no process launched)'; return }

$inventory = [ordered]@{
    recordedAt = (Get-Date).ToString('o')
    launcherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    launcherVersion = (Get-Item -LiteralPath $launcher).VersionInfo.ProductVersion
    host = $config.serverHost; port = 9010
}
[IO.File]::WriteAllText((Join-Path $root 'launcher-last-start.json'),
    ($inventory | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
# Explicit roots prevent migration from the default global OpenAC profile.
# --data-dir is application storage, NOT the Retail DAT input directory.
Push-Location $root
try {
    & $launcher --config-dir "$root\Config" --data-dir "$root\PreparedData" --cache-dir "$root\Cache"
} finally { Pop-Location }
