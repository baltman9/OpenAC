[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scratch = Join-Path $repo ('.test-out\evaluation-config\' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $scratch 'RetailSource'
$target = Join-Path $scratch 'IsolatedTest'
New-Item -ItemType Directory -Path $source -Force | Out-Null
$configPath = Join-Path $scratch 'evaluation.local.json'
$reader = Join-Path $PSScriptRoot 'Read-OpenACEvaluationConfig.ps1'
$utf8 = [Text.UTF8Encoding]::new($false)
$script:checks = 0

function New-Fixture {
    return @{sourceRoot=$source; testRoot=$target; serverHost='test.example.org'; serverPort=9010; serverName='Fixture'}
}
function Assert-Fixture([hashtable]$Config, [bool]$Accept) {
    [IO.File]::WriteAllText($configPath, ($Config | ConvertTo-Json), $utf8)
    $accepted = $false
    try { $null = & $reader -ConfigPath $configPath; $accepted = $true } catch { }
    $script:checks++
    if ($accepted -ne $Accept) { throw "Configuration regression: fixture $script:checks" }
}

Assert-Fixture (New-Fixture) $true
$fixture = New-Fixture; $fixture.serverPort = 9000; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.serverHost = '<TEST_SERVER_HOST>'; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.serverHost = 'https://test.example.org'; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.Remove('serverHost'); Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.serverHost = 123; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = $source; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = Join-Path $source 'Nested'; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = $scratch; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = [IO.Path]::GetPathRoot($source); Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = '.\Relative'; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = $source.ToUpperInvariant(); Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = Join-Path $target '..\RetailSource'; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.sourceRoot = Join-Path $scratch 'Missing'; Assert-Fixture $fixture $false
$fixture = New-Fixture; $fixture.testRoot = Join-Path $scratch 'RetailSource-Sibling'; Assert-Fixture $fixture $true

# Keep small generated fixtures in the ignored test-output directory for inspection.
Write-Host "EVALUATION-CONFIG-TESTS-OK ($script:checks fixtures; no DAT, profile or server changes)"
