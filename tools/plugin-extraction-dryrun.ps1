#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Rehearses moving a plugin out of this repository, in two halves, without
  touching the checkout.

.DESCRIPTION
  Half "without": exports the current commit into a scratch copy, deletes the
  plugin, its test project, their solution entries and the build rules that
  stage the plugin into a host's output, then builds the solution and runs the
  client's offline suites. Anything that breaks is a tie the client still has
  onto the plugin.

  Half "standalone": packs the plugin contract into a local folder feed, copies
  the plugin and its test project into an empty folder outside the repository
  with the few build files a repository of its own needs, then builds against
  the contract package and runs the plugin's tests. Anything that has to be
  added there is a file the new repository needs.

  Both halves are read-only with respect to the checkout: every copy, package
  and build output lands under -ScratchRoot.

.EXAMPLE
  pwsh tools/plugin-extraction-dryrun.ps1 -Plugin AcDream.Plugins.MossTank
#>
[CmdletBinding()]
param(
    # The plugin project's folder name under src/. Its test project is
    # expected at tests/<Plugin>.Tests.
    [Parameter(Mandatory = $true)]
    [string] $Plugin,

    # Which half to run.
    [ValidateSet('both', 'without', 'standalone')]
    [string] $Half = 'both',

    [string] $Configuration = 'Release',

    # Where the scratch copies go. Defaults to a folder beside the repository.
    [string] $ScratchRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ScratchRoot) {
    $ScratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) "acdream-extraction-dryrun"
}
$pluginDir = "src/$Plugin"
$pluginTestsDir = "tests/$Plugin.Tests"
$failures = New-Object System.Collections.Generic.List[string]

function Write-Section([string] $text) {
    Write-Host ''
    Write-Host "== $text" -ForegroundColor Cyan
}

function Invoke-Step([string] $label, [scriptblock] $body) {
    Write-Section $label
    & $body
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("$label (exit $LASTEXITCODE)")
        Write-Host "FAILED: $label" -ForegroundColor Red
    }
}

# A previous run's test host can still hold a file in the scratch copy, so a
# folder that refuses to go is stepped over rather than fought with.
function Clear-ScratchFolder([string] $destination) {
    if (-not (Test-Path $destination)) { return $destination }
    try {
        Remove-Item -Recurse -Force $destination -ErrorAction Stop
        return $destination
    }
    catch {
        for ($i = 2; $i -lt 100; $i++) {
            $candidate = "$destination-$i"
            if (-not (Test-Path $candidate)) {
                Write-Host "could not clear $destination; using $candidate"
                return $candidate
            }
        }
        throw "could not clear $destination"
    }
}

function New-CleanExport([string] $destination) {
    New-Item -ItemType Directory -Force $destination | Out-Null
    # git archive gives the committed tree with no bin/obj and no local edits.
    $bundle = Join-Path $ScratchRoot 'export.zip'
    Push-Location $repo
    try {
        git archive --format=zip -o $bundle HEAD
        if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    }
    finally { Pop-Location }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($bundle, $destination)
    Remove-Item -Force $bundle
}

# Drops every project element that names the plugin's folder, whether it is a
# one-line <Import ... /> or a <ProjectReference> with children.
function Remove-PluginElements([string] $path, [string] $plugin) {
    $text = [System.IO.File]::ReadAllText($path)
    $folder = [regex]::Escape($plugin)
    $before = $text
    foreach ($element in @('ProjectReference', 'Import', 'None', 'Content')) {
        $pattern = "(?s)[ \t]*<$element\b[^>]*?[\\/]$folder[\\/][^>]*?(/>|>.*?</$element>)\r?\n"
        $text = [regex]::Replace($text, $pattern, '')
    }
    if ($text -ne $before) { [System.IO.File]::WriteAllText($path, $text) }
}

function Remove-Lines([string] $path, [string[]] $patterns) {
    if (-not (Test-Path $path)) { return }
    $text = [System.IO.File]::ReadAllText($path)
    foreach ($pattern in $patterns) {
        $text = [regex]::Replace($text, "(?m)^.*$([regex]::Escape($pattern)).*\r?\n", '')
    }
    [System.IO.File]::WriteAllText($path, $text)
}

New-Item -ItemType Directory -Force $ScratchRoot | Out-Null

# ---------------------------------------------------------------- without ----
if ($Half -in @('both', 'without')) {
    $without = Clear-ScratchFolder (Join-Path $ScratchRoot 'without-plugin')
    Write-Section "Exporting the commit without $Plugin into $without"
    New-CleanExport $without

    Remove-Item -Recurse -Force (Join-Path $without $pluginDir)
    Remove-Item -Recurse -Force (Join-Path $without $pluginTestsDir)
    Remove-Lines (Join-Path $without 'AcDream.slnx') @("$pluginDir/", "$pluginTestsDir/")
    # Every project file that named the plugin loses what it named: the build
    # rules a host imported to stage it, and any reference or linked content
    # another project took from the plugin's folder.
    Get-ChildItem -Path $without -Recurse -File |
        Where-Object { $_.Extension -in '.csproj', '.props', '.targets' } |
        ForEach-Object { Remove-PluginElements $_.FullName $Plugin }

    Push-Location $without
    try {
        Invoke-Step 'build (without plugin)' { dotnet build AcDream.slnx -c $Configuration }
        foreach ($suite in @(
                'AcDream.Core.Tests', 'AcDream.Runtime.Tests', 'AcDream.App.Tests',
                'AcDream.Headless.Tests', 'AcDream.HostParity.Tests',
                'AcDream.Launcher.Core.Tests', 'AcDream.UI.Abstractions.Tests')) {
            $project = "tests/$suite/$suite.csproj"
            Invoke-Step "test $suite (without plugin)" {
                dotnet test $project -c $Configuration --no-build
            }
        }
    }
    finally { Pop-Location }
}

# ------------------------------------------------------------- standalone ----
if ($Half -in @('both', 'standalone')) {
    $feed = Join-Path $ScratchRoot 'contract-feed'
    $standalone = Join-Path $ScratchRoot 'standalone-plugin'
    $standalone = Clear-ScratchFolder $standalone
    New-Item -ItemType Directory -Force $feed, $standalone | Out-Null

    Write-Section 'Packing the plugin contract into a local folder feed'
    Push-Location $repo
    try {
        dotnet pack src/AcDream.Plugin.Abstractions/AcDream.Plugin.Abstractions.csproj `
            -c $Configuration -o $feed
        if ($LASTEXITCODE -ne 0) { throw 'packing the contract failed' }
    }
    finally { Pop-Location }
    $package = Get-ChildItem $feed -Filter 'AcDream.Plugin.Abstractions.*.nupkg' |
        Sort-Object Name | Select-Object -Last 1
    if (-not $package) { throw "no contract package in $feed" }
    $contractVersion = [regex]::Match(
        $package.Name, '^AcDream\.Plugin\.Abstractions\.(.+)\.nupkg$').Groups[1].Value
    Write-Host "contract package version: $contractVersion"

    Write-Section "Copying the plugin and its tests into $standalone"
    Copy-Item -Recurse (Join-Path $repo $pluginDir) (Join-Path $standalone "src/$Plugin")
    Copy-Item -Recurse (Join-Path $repo $pluginTestsDir) (Join-Path $standalone "tests/$Plugin.Tests")
    foreach ($stray in @('bin', 'obj')) {
        Get-ChildItem $standalone -Recurse -Directory -Filter $stray -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
    }

    # The few build files a repository of its own has to carry, because in this
    # repository they come from the root and the plugin inherits them.
    Copy-Item (Join-Path $repo 'global.json') (Join-Path $standalone 'global.json')
    @"
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AnalysisLevel>latest</AnalysisLevel>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <Version>$contractVersion</Version>
    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
    <UsePluginApiPackage>true</UsePluginApiPackage>
    <PluginApiPackageVersion>$contractVersion</PluginApiPackageVersion>
  </PropertyGroup>
</Project>
"@ | Set-Content -NoNewline -Path (Join-Path $standalone 'Directory.Build.props')

    # Central package versions, trimmed to what the plugin and its tests use.
    @"
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="AcDream.Plugin.Abstractions" Version="$contractVersion" />
    <PackageVersion Include="coverlet.collector" Version="6.0.4" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
"@ | Set-Content -NoNewline -Path (Join-Path $standalone 'Directory.Packages.props')

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="contract" value="$feed" />
  </packageSources>
</configuration>
"@ | Set-Content -NoNewline -Path (Join-Path $standalone 'NuGet.Config')

    @"
<Solution>
  <Folder Name="/src/">
    <Project Path="src/$Plugin/$Plugin.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/$Plugin.Tests/$Plugin.Tests.csproj" />
  </Folder>
</Solution>
"@ | Set-Content -NoNewline -Path (Join-Path $standalone 'Plugin.slnx')

    Push-Location $standalone
    try {
        Invoke-Step 'build (standalone)' { dotnet build Plugin.slnx -c $Configuration }
        Invoke-Step 'test (standalone)' {
            dotnet test "tests/$Plugin.Tests/$Plugin.Tests.csproj" -c $Configuration --no-build
        }
    }
    finally { Pop-Location }
}

Write-Section 'Summary'
if ($failures.Count -eq 0) {
    Write-Host 'every step passed' -ForegroundColor Green
    exit 0
}
foreach ($failure in $failures) { Write-Host "  $failure" -ForegroundColor Red }
exit 1
