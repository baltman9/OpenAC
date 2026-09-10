[CmdletBinding()]
param([string]$ConfigPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $PSScriptRoot 'evaluation.local.json' }

function Assert-RegularPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Evaluation paths must not contain links or junctions.'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

Assert-RegularPath $ConfigPath
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw 'Create ignored evaluation.local.json from evaluation.example.json and fill in your local paths and test host.'
}
$config = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json
foreach ($key in @('sourceRoot', 'testRoot', 'serverHost', 'serverName')) {
    if ($null -eq $config.PSObject.Properties[$key] -or
        $config.$key -isnot [string] -or [string]::IsNullOrWhiteSpace($config.$key) -or
        $config.$key -match '[<>\r\n]') {
        throw "Missing or placeholder evaluation configuration field: $key"
    }
}
if ($null -eq $config.PSObject.Properties['serverPort'] -or
    [string]$config.serverPort -cne '9010') {
    throw 'Evaluation is restricted to the isolated test server on port 9010.'
}
if ([Uri]::CheckHostName($config.serverHost) -eq [UriHostNameType]::Unknown) {
    throw 'serverHost must be a hostname or IP address, without a URL or port.'
}
foreach ($key in @('sourceRoot', 'testRoot')) {
    $expanded = [Environment]::ExpandEnvironmentVariables($config.$key)
    if ($expanded -notmatch '^[A-Za-z]:[\\/]' -or $expanded -match '%') {
        throw "$key must be an absolute Windows drive path with any environment variables resolved."
    }
    $resolved = [IO.Path]::GetFullPath($expanded).TrimEnd('\', '/')
    if ($resolved -eq [IO.Path]::GetPathRoot($expanded).TrimEnd('\', '/')) {
        throw "$key must not be a drive root."
    }
    Assert-RegularPath $resolved
    $config.$key = $resolved
}
$sourcePrefix = $config.sourceRoot.TrimEnd('\') + '\'
$testPrefix = $config.testRoot.TrimEnd('\') + '\'
if ($sourcePrefix.StartsWith($testPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $testPrefix.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Read-only source and isolated test roots must not overlap.'
}
if (-not (Test-Path -LiteralPath $config.sourceRoot -PathType Container)) {
    throw 'The read-only Retail source directory does not exist.'
}
return $config
