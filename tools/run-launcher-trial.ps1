[CmdletBinding()]
param(
    [string]$Root = 'artifacts/launcher-trial',
    [switch]$Clean,
    [switch]$NoLaunch,
    [string]$PluginListUri
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'run-launcher-trial requires PowerShell 7 or newer.'
}

$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path (Join-Path $RepoRoot 'AcDream.slnx'))) {
    throw "Could not locate AcDream.slnx above '$PSScriptRoot'."
}

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$ridArchitecture = switch ($architecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw "run-launcher-trial does not support processor architecture $architecture." }
}
$ridOs =
    if ($IsWindows) { 'win' }
    elseif ($IsMacOS) { 'osx' }
    elseif ($IsLinux) { 'linux' }
    else { throw 'Unrecognised operating system; run-launcher-trial supports Windows, macOS and Linux only.' }
$Rid = "$ridOs-$ridArchitecture"
$supportedRids = @('win-x64', 'osx-arm64', 'osx-x64', 'linux-x64')
if ($supportedRids -notcontains $Rid) {
    throw "run-launcher-trial does not support RID '$Rid'. Supported: $($supportedRids -join ', ')."
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Raw
$repositoryVersion = [string]$buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($repositoryVersion)) {
    throw 'Directory.Build.props has no <Version>.'
}
# Build metadata sorts equal to the release (SemVer 2.0 precedence ignores
# anything after '+'), so the launcher neither offers a downgrade nor treats
# a plugin's minHostVersion as unmet against this trial install.
$TrialVersion = "$repositoryVersion+trial"

$ScratchRoot = if ([IO.Path]::IsPathRooted($Root)) {
    [IO.Path]::GetFullPath($Root)
} else {
    [IO.Path]::GetFullPath((Join-Path $RepoRoot $Root))
}

if ($Clean -and (Test-Path -LiteralPath $ScratchRoot)) {
    Remove-Item -LiteralPath $ScratchRoot -Recurse -Force
}

$ConfigDir = Join-Path $ScratchRoot 'config'
$DataDir = Join-Path $ScratchRoot 'data'
$CacheDir = Join-Path $ScratchRoot 'cache'
$ClientBuildDir = Join-Path $ScratchRoot 'build/client'
$LauncherBuildDir = Join-Path $ScratchRoot 'build/launcher'

foreach ($directory in @($ConfigDir, $DataDir, $CacheDir)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

Write-Host 'acdream launcher trial' -ForegroundColor Cyan
Write-Host "  rid     : $Rid"
Write-Host "  version : $repositoryVersion (installed as $TrialVersion)"
Write-Host "  root    : $ScratchRoot"
Write-Host ''

function Invoke-TrialPublish {
    param([Parameter(Mandatory)][string]$Project, [Parameter(Mandatory)][string]$OutputDirectory)

    $arguments = @(
        'publish', (Join-Path $RepoRoot $Project),
        '-c', 'Release',
        '-r', $Rid,
        '--self-contained', 'true',
        "-p:Version=$repositoryVersion",
        # No SourceLink '+<sha>' suffix: LauncherVersion parses this as SemVer.
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-o', $OutputDirectory,
        '--nologo'
    )
    & dotnet @arguments | Out-Null
    if ($LASTEXITCODE) { throw "publish failed: $Project ($Rid)" }
}

function Remove-TrialDebugSymbols {
    param([Parameter(Mandatory)][string]$Directory)

    Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter *.pdb |
        Remove-Item -Force
}

# --- client: App + Headless, self-contained, one directory -----------------
if (Test-Path -LiteralPath $ClientBuildDir) { Remove-Item -LiteralPath $ClientBuildDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ClientBuildDir | Out-Null

Write-Host "[$Rid] publishing client (App + Headless)..." -ForegroundColor Yellow
Invoke-TrialPublish 'src/AcDream.App/AcDream.App.csproj' $ClientBuildDir
Invoke-TrialPublish 'src/AcDream.Headless/AcDream.Headless.csproj' $ClientBuildDir

if ($Rid -like 'osx-*') {
    $publishedAppHost = Join-Path $ClientBuildDir 'AcDream.App'
    $macClient = Join-Path $ClientBuildDir 'acdream-client'
    if (-not (Test-Path -LiteralPath $publishedAppHost -PathType Leaf)) {
        throw "macOS graphical client publish output '$publishedAppHost' is missing."
    }
    Move-Item -LiteralPath $publishedAppHost -Destination $macClient -Force

    if ($Rid -eq 'osx-arm64') {
        & (Join-Path $RepoRoot 'tools/package-macos-vulkan.ps1') -ClientDirectory $ClientBuildDir
        if ($LASTEXITCODE) { throw 'macOS Vulkan dependency packaging failed.' }
    } else {
        # osx-x64: same client staging as arm64, then the pinned x86_64 Vulkan runtime.
        $runtime = Join-Path $ScratchRoot 'build/macos-x64-vulkan'
        & (Join-Path $RepoRoot 'tools/build-macos-x64-vulkan.ps1') -OutputDirectory $runtime
        & (Join-Path $RepoRoot 'tools/package-macos-x64-vulkan.ps1') `
            -ClientDirectory $ClientBuildDir -VulkanRuntimeDirectory $runtime
        if ($LASTEXITCODE) { throw 'osx-x64 Vulkan dependency packaging failed.' }
    }
}

Remove-TrialDebugSymbols $ClientBuildDir

$clientExecutables = if ($Rid -like 'osx-*') {
    @('acdream-client', 'acdream-headless')
} else {
    $suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
    @("AcDream.App$suffix", "acdream-headless$suffix")
}
foreach ($required in $clientExecutables) {
    if (-not (Test-Path -LiteralPath (Join-Path $ClientBuildDir $required) -PathType Leaf)) {
        throw "Published client is missing required executable '$required'."
    }
}

# --- install the client as the launcher's current client version -----------
function Install-TrialClient {
    param(
        [Parameter(Mandatory)][string]$SourceDirectory,
        [Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$Rid,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string[]]$RequiredExecutables
    )

    # Layout matches AcDream.Launcher.Core.Updates.ClientVersionStore: each
    # version lives in its own directory under 'app', carrying an install.json
    # of per-file sha256/size/unixMode, activated by a sibling current.json.
    $appDirectory = Join-Path $DataDirectory 'app'
    $versionDirectory = Join-Path $appDirectory $Version
    if (Test-Path -LiteralPath $versionDirectory) {
        Remove-Item -LiteralPath $versionDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $appDirectory | Out-Null
    Copy-Item -LiteralPath $SourceDirectory -Destination $versionDirectory -Recurse -Force

    # ClientVersionStore never re-extracts this archive; it only validates the
    # files already on disk against the per-file records below. Hashing a real
    # zip of the payload still gives archiveSha256/archiveSize honest values.
    $tempZip = Join-Path ([IO.Path]::GetTempPath()) "acdream-trial-$([Guid]::NewGuid().ToString('N')).zip"
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $versionDirectory, $tempZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archiveSha256 = (Get-FileHash -LiteralPath $tempZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveSize = (Get-Item -LiteralPath $tempZip).Length
    Remove-Item -LiteralPath $tempZip -Force

    $requiredSet = [Collections.Generic.HashSet[string]]::new(
        [string[]]$RequiredExecutables, [StringComparer]::Ordinal)
    $records = [Collections.Generic.List[object]]::new()
    Get-ChildItem -LiteralPath $versionDirectory -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($versionDirectory, $_.FullName).Replace('\', '/')
        # 0755 for the two required executables, 0644 for everything else -
        # the same split a released zip's entries carry (publish-bin.ps1).
        $unixMode = if ($requiredSet.Contains($relative)) { 493 } else { 420 }
        if (-not $IsWindows) {
            [IO.File]::SetUnixFileMode($_.FullName, [IO.UnixFileMode]$unixMode)
        }
        $records.Add([ordered]@{
            path     = $relative
            sha256   = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            size     = $_.Length
            unixMode = $unixMode
        })
    }

    # ClientVersionStore.ValidateRecord requires the file list in strict
    # ordinal order; PowerShell's Sort-Object is culture-aware by default and
    # a Swedish host must not reorder this.
    $sortedRecords = [Linq.Enumerable]::OrderBy(
        $records, [Func[object, string]] { param($record) $record.path }, [StringComparer]::Ordinal)

    $record = [ordered]@{
        schemaVersion = 1
        version       = $Version
        rid           = $Rid
        archiveSha256 = $archiveSha256
        archiveSize   = $archiveSize
        files         = @($sortedRecords)
    }
    $recordJson = $record | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText(
        (Join-Path $versionDirectory 'install.json'),
        $recordJson + "`n",
        [Text.UTF8Encoding]::new($false))

    $pointer = [ordered]@{
        schemaVersion  = 1
        currentVersion = $Version
    }
    $pointerJson = $pointer | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText(
        (Join-Path $appDirectory 'current.json'),
        $pointerJson + "`n",
        [Text.UTF8Encoding]::new($false))
}

Write-Host "[$Rid] installing trial client as $TrialVersion..." -ForegroundColor Yellow
Install-TrialClient -SourceDirectory $ClientBuildDir -DataDirectory $DataDir `
    -Rid $Rid -Version $TrialVersion -RequiredExecutables $clientExecutables

# --- launcher ----------------------------------------------------------------
if (Test-Path -LiteralPath $LauncherBuildDir) { Remove-Item -LiteralPath $LauncherBuildDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $LauncherBuildDir | Out-Null

Write-Host "[$Rid] publishing launcher (+ co-deployed bake)..." -ForegroundColor Yellow
Invoke-TrialPublish 'src/AcDream.Launcher/AcDream.Launcher.csproj' $LauncherBuildDir

$launcherSuffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
$launcherExecutable = Join-Path $LauncherBuildDir "acdream-launcher$launcherSuffix"
if (-not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf)) {
    throw "Published launcher is missing '$launcherExecutable'."
}
if (-not $IsWindows) {
    [IO.File]::SetUnixFileMode($launcherExecutable, [IO.UnixFileMode]493)
}

# --- run, isolated from the tester's real install -----------------------
$launchEnvironment = [ordered]@{
    ACDREAM_CONFIG_DIR = $ConfigDir
    ACDREAM_DATA_DIR   = $DataDir
    ACDREAM_CACHE_DIR  = $CacheDir
}
$launcherArguments = [Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($PluginListUri)) {
    $launcherArguments.Add('--plugin-list-uri')
    $launcherArguments.Add($PluginListUri)
}

Write-Host ''
Write-Host 'Trial staged:' -ForegroundColor Green
Write-Host "  scratch root    : $ScratchRoot"
Write-Host "  client version  : $TrialVersion ($Rid)"
Write-Host "  launcher        : $launcherExecutable"

if ($NoLaunch) {
    $envDisplay = ($launchEnvironment.GetEnumerator() |
        ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    $argumentDisplay = ($launcherArguments -join ' ')
    Write-Host ''
    Write-Host '-NoLaunch: not starting the launcher. Command it would run:' -ForegroundColor Cyan
    Write-Host "  $envDisplay `"$launcherExecutable`" $argumentDisplay"
    return
}

$startInfo = [Diagnostics.ProcessStartInfo]::new($launcherExecutable)
foreach ($argument in $launcherArguments) { $startInfo.ArgumentList.Add($argument) }
foreach ($key in $launchEnvironment.Keys) { $startInfo.Environment[$key] = $launchEnvironment[$key] }
$startInfo.UseShellExecute = $false

Write-Host ''
Write-Host 'Starting the launcher...' -ForegroundColor Yellow
$process = [Diagnostics.Process]::Start($startInfo)
Write-Host "  pid: $($process.Id)"
