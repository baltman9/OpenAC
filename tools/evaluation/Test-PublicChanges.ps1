[CmdletBinding()]
param(
    [string]$BaseRef = 'refs/remotes/upstream/main',
    [string]$Revision = 'HEAD',
    [switch]$Staged,
    [switch]$SelfTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Deliberately generic: the guard itself must not contain private denylist values.
function Get-PrivacyIssue([string]$Text) {
    if ($Text -match '(?i)[a-z]:[\\/]+Users[\\/]+(?!Public(?:[\\/]|$)|Default(?:[\\/]|$))[^\s<>%"''\\/]+') {
        return 'personal Windows home path'
    }
    if ($Text -match '/(?:home|Users)/[^/\s<>$"''`\\]+') { return 'personal Unix home path' }
    if ($Text -match '(?<![\d.])(?:10(?:\.\d{1,3}){3}|192\.168(?:\.\d{1,3}){2}|172\.(?:1[6-9]|2\d|3[01])(?:\.\d{1,3}){2})(?![\d.])') {
        return 'private IPv4 address'
    }
    foreach ($match in [regex]::Matches($Text, '(?i)[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}')) {
        if ($match.Value -notmatch '(?i)@(?:users\.noreply\.github\.com|(?:[a-z0-9-]+\.)*example\.(?:com|org|net))$') {
            return 'non-placeholder email address'
        }
    }
    if ($Text -match '-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----' -or
        $Text -match '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})' -or
        $Text -match '(?i)https?://[^\s/:]+:[^\s/@]+@' -or
        $Text -match '(?i)["'']?(?:password|passwd|api[_-]?key|access[_-]?token)["'']?\s*[:=]\s*["''](?![<$%]|["'']|(?:example|placeholder|test-only))[a-z0-9][^"'']{5,}["'']') {
        return 'possible credential or private key'
    }
    return $null
}

function Test-PrivateArtifact([string]$Path) {
    return $Path -match '(?i)(?:^|/)(?:evaluation\.local\.json|launcher-profiles\.json[^/]*|logs|artifacts|\.env(?:\.[^/]*)?)(?:/|$)|\.(?:dat|pak|bundle|dmp|jsonl|log|png|jpe?g|gif|webp|zip|7z|pfx|p12|pem)$'
}

function Test-PublicIdentity([string]$Name, [string]$Email) {
    if ($Email -notmatch '^(?:\d+\+)?([A-Za-z0-9-]+)@users\.noreply\.github\.com$') { return $false }
    return $Name -ceq $Matches[1]
}

if ($SelfTest) {
    # Synthetic fixtures are assembled so no actual private example is published.
    $cases = @(
        @{Text=('C:' + '\Users\' + 'fixture-user\project'); Bad=$true},
        @{Text=('C:' + '\\Users\\' + 'fixture-user\\project'); Bad=$true},
        @{Text=('/home/' + 'fixture-user/project'); Bad=$true},
        @{Text=('192.' + '168.5.12'); Bad=$true},
        @{Text=('10.' + '5.6.7'); Bad=$true},
        @{Text=('172.' + '16.0.2'); Bad=$true},
        @{Text=('172.' + '31.0.2'); Bad=$true},
        @{Text=('private-person' + '@' + 'mail.invalid'); Bad=$true},
        @{Text=('ghp_' + ('a' * 30)); Bad=$true},
        @{Text=('-----BEGIN ' + 'PRIVATE KEY-----'); Bad=$true},
        @{Text=('password = "' + 'not-a-real-secret' + '"'); Bad=$true},
        @{Text='%USERPROFILE%\Documents\Projects\OpenAC'; Bad=$false},
        @{Text='$env:USERPROFILE'; Bad=$false},
        @{Text='<TEST_SERVER_HOST>:9010'; Bad=$false},
        @{Text='contact@example.org'; Bad=$false},
        @{Text='123+fixture-user@users.noreply.github.com'; Bad=$false},
        @{Text='C:\Games\OpenAC-Test'; Bad=$false},
        @{Text='https://github.com/eriknihlen/OpenAC'; Bad=$false}
    )
    $index = 0
    foreach ($case in $cases) {
        $index++
        if ([bool](Get-PrivacyIssue $case.Text) -ne $case.Bad) { throw "Privacy self-test failed: fixture $index" }
    }
    foreach ($path in @('nested/evaluation.local.json', 'capture.png', 'local.bundle', 'logs/session.txt')) {
        if (-not (Test-PrivateArtifact $path)) { throw 'Private-artifact self-test failed.' }
    }
    if ((Test-PrivateArtifact 'evaluation.example.json') -or
        -not (Test-PublicIdentity 'fixture-user' '123+fixture-user@users.noreply.github.com') -or
        (Test-PublicIdentity 'Private Person' '123+fixture-user@users.noreply.github.com')) {
        throw 'Public example or commit identity self-test failed.'
    }
    Write-Host 'PUBLIC-CHANGES-SELF-TEST-OK'
    return
}

function Invoke-Git([string[]]$Arguments) {
    $output = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Git inspection failed; privacy check cannot approve this push.' }
    return $output
}

$problems = [Collections.Generic.List[string]]::new()
function Inspect-Text([string]$Text, [string]$Location) {
    $issue = Get-PrivacyIssue $Text
    if ($issue) { $problems.Add("${Location}: $issue (value withheld)") }
}
function Inspect-Diff([string[]]$DiffArguments, [string]$Label) {
    $files = @(Invoke-Git (@('diff', '--name-only', '--diff-filter=ACMRT') + $DiffArguments + @('--')))
    foreach ($file in $files) {
        # Paths can themselves be private; diagnostics use a file ordinal instead.
        $location = "$Label file $([array]::IndexOf($files, $file) + 1)"
        Inspect-Text $file $location
        if (Test-PrivateArtifact $file) { $problems.Add("${location}: private artifact or image requires review") }
    }
    $patch = @(Invoke-Git (@('diff', '--no-ext-diff', '--no-textconv', '--no-color', '--unified=0') + $DiffArguments + @('--')))
    $fileNumber = 0
    $lineNumber = 0
    foreach ($line in $patch) {
        if ($line.StartsWith('diff --git ')) { $fileNumber++; continue }
        if ($line -match '^Binary files .* differ$' -or $line -eq 'GIT binary patch') {
            $problems.Add("$Label patch file ${fileNumber}: binary change requires review")
        }
        if ($line -match '^@@ .* \+(\d+)') { $lineNumber = [int]$Matches[1]; continue }
        if ($line.StartsWith('+++')) { continue }
        if ($line.StartsWith('+')) {
            Inspect-Text $line.Substring(1) "$Label patch file $fileNumber line $lineNumber"
            $lineNumber++
        } elseif ($line.StartsWith(' ')) { $lineNumber++ }
    }
}

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $repo
try {
    $base = [string](Invoke-Git @('rev-parse', '--verify', "${BaseRef}^{commit}"))
    $tip = [string](Invoke-Git @('rev-parse', '--verify', "${Revision}^{commit}"))
    $commits = @(Invoke-Git @('rev-list', $tip, '--not', $base))
    foreach ($commit in $commits) {
        $label = $commit.Substring(0, 12)
        $metadata = (Invoke-Git @('show', '-s', '--format=%an%x00%ae%x00%cn%x00%ce%x00%B', $commit)) -join "`n"
        $parts = $metadata.Split([char]0)
        if ($parts.Count -ne 5) { throw 'Unexpected commit metadata; refusing to approve.' }
        if (-not (Test-PublicIdentity $parts[0] $parts[1]) -or
            -not (Test-PublicIdentity $parts[2] $parts[3])) {
            $problems.Add("${label}: our commit identity must use a public handle and matching GitHub no-reply email")
        }
        Inspect-Text $parts[4] "$label commit message"
        $parents = ([string](Invoke-Git @('rev-list', '--parents', '-n', '1', $commit))).Split(' ')
        if ($parents.Count -eq 2) {
            Inspect-Diff @($parents[1], $commit) $label
        } elseif ($parents.Count -gt 2) {
            # Compare a merge to its automatic remerge: inspect conflict resolutions
            # without reclassifying unchanged upstream additions as our work.
            $mergePatch = @(Invoke-Git @('show', '--format=', '--remerge-diff', '--no-ext-diff', '--no-textconv', '--no-color', $commit))
            foreach ($line in $mergePatch) {
                if ($line -match '^Binary files .* differ$' -or $line -eq 'GIT binary patch') {
                    $problems.Add("${label}: binary merge resolution requires review")
                }
                if ($line.StartsWith('+++ b/') -and (Test-PrivateArtifact $line.Substring(6))) {
                    $problems.Add("${label}: private artifact in merge resolution requires review")
                }
                if ($line.StartsWith('+') -and -not $line.StartsWith('+++')) {
                    Inspect-Text $line.Substring(1) "$label merge resolution"
                }
            }
        } else { throw 'Unexpected fork root commit; review its provenance before pushing.' }
    }
    $mergeBase = [string](Invoke-Git @('merge-base', $base, $tip))
    Inspect-Diff @($mergeBase, $tip) 'final tree'
    if ($Staged) {
        if ($Revision -cne 'HEAD') { throw '-Staged only supports the HEAD revision.' }
        Inspect-Diff @('--cached', 'HEAD') 'staged changes'
    }
    if ($problems.Count -gt 0) {
        foreach ($problem in ($problems | Select-Object -Unique)) { Write-Warning $problem }
        throw 'PUBLIC-CHANGES-BLOCKED: review the flagged diff/history without publishing matched values.'
    }
    Write-Host "PUBLIC-CHANGES-OK ($($commits.Count) fork-only commits checked)"
} finally { Pop-Location }
