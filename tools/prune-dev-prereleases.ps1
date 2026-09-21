<#
.SYNOPSIS
    Deletes superseded development pre-releases and their tags, keeping the newest few.

.DESCRIPTION
    Only a release whose tag reads v<major>.<minor>.<patch>-dev.<n> is ever a
    candidate. A stable release cannot match that shape, so this never touches
    one, and the release list is filtered to pre-releases before matching as
    well: two independent reasons for the same guarantee.

    Ranking is by version precedence, not by publication date, so a pre-release
    published out of order is still ranked where its version says it belongs:
    v0.1.10-dev.1 is newer than v0.1.9-dev.2, and dev.10 is newer than dev.9.

    Needs the GitHub CLI, authenticated (in CI, through GH_TOKEN and GH_REPO).
    Use -WhatIf to see what would be deleted without deleting anything, and
    -SelfTest to check the selection rule alone, with no network access.

.EXAMPLE
    ./tools/prune-dev-prereleases.ps1 -Keep 5 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # How many of the newest development pre-releases survive.
    [ValidateRange(1, 100)][int]$Keep = 5,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'prune-dev-prereleases requires PowerShell 7 or newer.'
}

# A development pre-release tag and nothing else. The '-dev.' part is what
# separates this train from a stable release and from any other pre-release
# someone might publish by hand.
$DevPreReleaseTagPattern =
    '^v(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)-dev\.(?<build>0|[1-9]\d*)$'

<#
.SYNOPSIS
    The development pre-release tags that fall outside the newest $Keep.

.DESCRIPTION
    Pure: it reads nothing and deletes nothing. Input order does not matter and
    anything that is not a development pre-release tag is dropped. Returns the
    superseded tags, newest first.
#>
function Select-SupersededDevPreReleases {
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][AllowNull()][string[]]$Tags,
        [Parameter(Mandatory)][int]$Keep
    )

    if ($Keep -lt 1) {
        throw 'Keep must be at least 1: this rule never deletes every pre-release.'
    }

    $candidates = [Collections.Generic.List[object]]::new()
    foreach ($tag in @($Tags)) {
        if ([string]::IsNullOrWhiteSpace($tag)) { continue }
        $match = [Text.RegularExpressions.Regex]::Match($tag, $DevPreReleaseTagPattern)
        if (-not $match.Success) { continue }

        # Fixed-width digits so that comparing the keys as text compares the
        # versions as numbers: 000000010 sorts above 000000009.
        $key = '{0:D9}.{1:D9}.{2:D9}.{3:D9}' -f @(
            [int]$match.Groups['major'].Value,
            [int]$match.Groups['minor'].Value,
            [int]$match.Groups['patch'].Value,
            [int]$match.Groups['build'].Value)
        $candidates.Add([pscustomobject]@{ Tag = $tag; Key = $key })
    }

    if ($candidates.Count -le $Keep) { return @() }

    # Ordinal, not culture-aware: PowerShell's Sort-Object follows the host's
    # culture, and the answer must not depend on which machine ran the job.
    $ordered = [Linq.Enumerable]::ToArray(
        [Linq.Enumerable]::OrderByDescending(
            [object[]]$candidates.ToArray(),
            [Func[object, string]] { param($candidate) $candidate.Key },
            [StringComparer]::Ordinal))

    return @($ordered | Select-Object -Skip $Keep | ForEach-Object { $_.Tag })
}

function Assert-SelectionCase {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[string]]$Failures,
        [Parameter(Mandatory)][string]$Because,
        [AllowEmptyCollection()][AllowNull()][string[]]$Tags,
        [Parameter(Mandatory)][int]$Keep,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Expected
    )

    $actual = @(Select-SupersededDevPreReleases -Tags $Tags -Keep $Keep)
    $expectedText = ($Expected -join ', ')
    $actualText = ($actual -join ', ')
    if ($actualText -cne $expectedText) {
        $Failures.Add("$Because -- expected [$expectedText], got [$actualText]")
    }
}

function Invoke-SelectionSelfTest {
    $failures = [Collections.Generic.List[string]]::new()

    Assert-SelectionCase $failures 'fewer pre-releases than the keep count deletes nothing' `
        @('v0.1.13-dev.1', 'v0.1.13-dev.2') 5 @()

    Assert-SelectionCase $failures 'exactly the keep count deletes nothing' `
        @('v0.1.13-dev.1', 'v0.1.13-dev.2', 'v0.1.13-dev.3') 3 @()

    Assert-SelectionCase $failures 'no pre-releases at all deletes nothing' `
        @() 5 @()

    Assert-SelectionCase $failures 'the oldest beyond the keep count are selected, newest first' `
        @('v0.1.13-dev.1', 'v0.1.13-dev.2', 'v0.1.13-dev.3', 'v0.1.13-dev.4',
          'v0.1.13-dev.5', 'v0.1.13-dev.6', 'v0.1.13-dev.7') 5 `
        @('v0.1.13-dev.2', 'v0.1.13-dev.1')

    Assert-SelectionCase $failures 'input order does not change the answer' `
        @('v0.1.13-dev.4', 'v0.1.13-dev.1', 'v0.1.13-dev.7', 'v0.1.13-dev.3',
          'v0.1.13-dev.6', 'v0.1.13-dev.2', 'v0.1.13-dev.5') 5 `
        @('v0.1.13-dev.2', 'v0.1.13-dev.1')

    Assert-SelectionCase $failures 'ranking is numeric, not lexical: dev.10 is newer than dev.9' `
        @('v0.1.13-dev.9', 'v0.1.13-dev.10') 1 @('v0.1.13-dev.9')

    Assert-SelectionCase $failures 'ranking is numeric across the version core too' `
        @('v0.1.9-dev.2', 'v0.1.10-dev.1') 1 @('v0.1.9-dev.2')

    Assert-SelectionCase $failures 'a major or minor bump outranks a higher patch' `
        @('v0.1.99-dev.1', 'v0.2.0-dev.1', 'v1.0.0-dev.1') 1 `
        @('v0.2.0-dev.1', 'v0.1.99-dev.1')

    Assert-SelectionCase $failures 'stable tags are never candidates' `
        @('v0.1.11', 'v0.1.12', 'v0.1.13', 'v0.1.13-dev.1', 'v0.1.13-dev.2') 1 `
        @('v0.1.13-dev.1')

    Assert-SelectionCase $failures 'a pre-release that is not this train is left alone' `
        @('v0.1.13-beta.1', 'v0.1.13-rc.1', 'v0.1.13-dev.1', 'v0.1.13-dev.2') 1 `
        @('v0.1.13-dev.1')

    Assert-SelectionCase $failures 'a malformed tag is ignored rather than ranked' `
        @('dev.1', 'v0.1-dev.1', 'v0.1.13-dev', 'v0.1.13-dev.01', 'v0.1.13-dev.1') 1 @()

    $threw = $false
    try { $null = Select-SupersededDevPreReleases -Tags @('v0.1.13-dev.1') -Keep 0 }
    catch { $threw = $true }
    if (-not $threw) { $failures.Add('a keep count below one must be refused') }

    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
        throw "prune-dev-prereleases selection self-test: $($failures.Count) failure(s)."
    }

    Write-Host 'prune-dev-prereleases selection self-test: all cases pass.' -ForegroundColor Green
}

if ($SelfTest) {
    Invoke-SelectionSelfTest
    return
}

$releaseList = & gh release list --limit 200 --json tagName,isPrerelease,isDraft
if ($LASTEXITCODE) {
    throw 'Could not list the repository releases through the GitHub CLI.'
}

$releases = @(($releaseList | Out-String) | ConvertFrom-Json)
$preReleaseTags = @(
    $releases |
        Where-Object { $_.isPrerelease -and -not $_.isDraft } |
        ForEach-Object { [string]$_.tagName })

$superseded = @(Select-SupersededDevPreReleases -Tags $preReleaseTags -Keep $Keep)

Write-Host "development pre-releases : $($preReleaseTags.Count) pre-release(s) listed, keeping $Keep"
if ($superseded.Count -eq 0) {
    Write-Host '  nothing superseded; no release or tag was touched.'
    return
}

foreach ($tag in $superseded) {
    if ($PSCmdlet.ShouldProcess($tag, 'Delete this pre-release and its tag')) {
        # --cleanup-tag: the tag goes with the release, so a pruned version
        # cannot be resurrected by a stale reference to its tag.
        & gh release delete $tag --yes --cleanup-tag
        if ($LASTEXITCODE) { throw "Could not delete pre-release '$tag'." }
        Write-Host "  deleted $tag and its tag"
    } else {
        Write-Host "  would delete $tag and its tag"
    }
}