# Isolated OpenAC evaluation (baltman9 fork)

Latest result: [0.1.1 manual Retail smoke-test pass and review caveats](openac-evaluation-smoke-2026-09-10.md).

## Branch and upstream policy

Work on `dev/openac-evaluation`; keep `main` for reviewed milestones. Normal
development commits and upstream integrations go to the development branch.
Do not merge to or push `main` merely because a setup step finished.

- `origin`: https://github.com/baltman9/OpenAC.git
- `upstream`: https://github.com/eriknihlen/OpenAC.git
- Example local checkout: `%USERPROFILE%\Documents\Projects\OpenAC`

The user approved staying current with upstream. At the start of development,
fetch `upstream`, inspect its changes, and integrate on this branch. Use a
fast-forward when possible; after divergence use a reviewed normal merge.
Never force-push or reset away our work without explicit approval for that
operation. Keep fixes focused for upstream PRs.
No scheduled synchronization or automatic milestone promotion is configured.

## Setup baseline, 2026-09-10

- Fork `main` preserved at `3b6bb58865a1997dc2831d845699c1fa3ad28a44`.
- Development source synchronized to upstream
  `a5a6e5d38416f159228c50a55da7b2721d32d63f` (source version 0.1.2), including
  the release-CI corrections published during setup. A separately approved
  privacy cleanup consolidates our evaluation changes into one sanitized commit
  on that upstream base. Upstream history and fork `main` remain untouched.
- Latest published Windows launcher available during setup: **v0.1.1**,
  corresponding to source `1e575c4d6f592e623081b32b4efb6e1cb86a2f4c`.
  This is an upstream release binary, not a build from our development branch.
- Archive: `launcher-win-x64.zip` from the
  [official v0.1.1 release](https://github.com/eriknihlen/OpenAC/releases/tag/v0.1.1).
- Archive SHA-256 verified against the GitHub release asset digest:
  `162F35C1E0E23AE7A9CAC7DA5F864CA02DFB7C58E15E2F2FBEC74B0DE9947B3A`.
- Extracted launcher SHA-256:
  `1BA0B2464CF5582EF1ED16A050EC0D77CB45CC70E4592772A6E73DC346749A30`.
- Extracted bake executable SHA-256:
  `9D9D149D3F612E7E7B0210FB6729ADFDBC7238C1CEC1477E9625E81E85D9F5A9`.

Source and published binaries may move at different times. The official
launcher remains on the upstream update feed and may install a newer release
when opened. Record actual launcher/client versions for each run. The wrapper
records the launcher's pre-start version/hash; updates during launch require
noting the new version separately.

## Runtime isolation

The following paths are examples, not a record of a contributor's workstation.
Copy `tools/evaluation/evaluation.example.json` to ignored
`tools/evaluation/evaluation.local.json` and fill in the read-only Retail source,
isolated test root and test server host. The file is required: scripts do not
embed or guess a private endpoint. Port 9010 remains mandatory. Never commit
the filled-in file. An alternate local file may be passed with `-ConfigPath`.

```text
C:\Games\OpenAC-Test\
    Launcher\         Official self-contained launcher and bake executable
    RetailDats\       Four copied Retail DATs; no ACME overrides
    PreparedData\     Application storage, prepared packages and logs
    Config\           Separate launcher profiles/settings (sensitive)
    Cache\            Separate cache and update storage
    Downloads\        Preserved original release archive
    Start-OpenACTestLauncher.ps1
    Initialize-OpenACTest.ps1
    Read-OpenACEvaluationConfig.ps1
    evaluation.local.json    Private machine configuration, never publish
    retail-dats.json
    openac-evaluation-root.txt
```

The initializer reads the configured original Retail folder but never writes
to it. Source and test roots must be separate, non-overlapping directories, not
drive roots or linked paths. It copies only `client_portal.dat`, `client_cell_1.dat`,
`client_highres.dat`, and `client_local_English.dat`, using the exact hashes in
`tools/evaluation/retail-dats.json`. Source and destination hashes are checked
after copying. Existing different DATs or an unrecognized target root cause a
stop, not an overwrite. No executable, DLL, plugin or preference is copied from
Turbine. Existing ACME clients and the shelved Vitaeum setup remain intact.

The seed profile contains only the locally configured test server, at
`<TEST_SERVER_HOST>:9010`, with zero accounts. No credentials are imported.
The start wrapper rejects a profile with additional servers or another endpoint,
and supplies all three explicit application roots. This is a launch-time check,
not a firewall: do not change the endpoint inside the open launcher.
Port 9000 and server configuration are unchanged.

`--data-dir` is application storage, not the DAT input directory. The prepared
package normally goes to `PreparedData\pak\acdream.pak`. Choose `RetailDats`
separately inside the launcher UI.

## First launch

For a standalone runtime copy, copy the initializer, launcher wrapper, config
reader and `retail-dats.json` from `tools/evaluation` into the isolated runtime
root, together with the private `evaluation.local.json`. These scripts also work
directly from the checkout when its ignored local configuration is present.

Close other test clients. In PowerShell (no elevation required):

```powershell
Set-Location C:\Games\OpenAC-Test
.\Start-OpenACTestLauncher.ps1 -CheckOnly
.\Start-OpenACTestLauncher.ps1
```

Always use this wrapper, not the executable directly, to preserve separate
settings roots. Allow the launcher to install/verify its client and record its
version. Select `C:\Games\OpenAC-Test\RetailDats` and let it prepare the package.
Do not point it at a Turbine directory. Use the seeded 9010 server and a dedicated
test account/password. Treat profiles and session files as sensitive; never
commit them or import Thwarg profiles. Leave plugins/render packs disabled.

The user performs the first gameplay check: login to town, window/camera check,
brief idle, normal exit, then repeat login. After that passes, test town ->
Town Network -> `/lifestone` -> town. Do not run ACME debugger/memory/checkpoint
scripts against OpenAC or replace the Retail highres DAT with an ACME version.

## Verification and remaining gates

Passed: PowerShell parsing, Windows PowerShell 5.1 preflight, repeat initialization,
four Retail copy/source hashes, and the official launcher's display-free
`--verify-publish` probe (exit 0). No graphical launcher, gameplay or server
connection was started during setup. No content package has been baked yet.

No .NET SDK was found, so no source build or .NET test-suite pass is claimed.
The packaged launcher includes its runtime. A source-build baseline needs the
SDK band in `global.json`, followed by the build and portable test gate in
`docs/building-and-running.md`.

Next boundaries: first-run Retail compatibility; source build/tests; then one
controlled HD texture experiment with prepared-package validation. Do not
migrate all textures or promote to `main` before relevant gates are reviewed.
Preserve ACME as a fallback.

## Public-repository hygiene

- Use `%USERPROFILE%` in Windows path examples and `$env:USERPROFILE` in
  PowerShell commands, not a named personal directory. Keep workstation paths,
  private hosts, accounts, personal names/emails and precise session logs local.
- Use a public GitHub handle and the no-reply email from GitHub email settings for
  our commit author and committer identity. Preserve upstream attribution.
- Keep filled-in configuration, profiles, screenshots and logs outside Git or
  ignored. `.gitignore` does not protect files already tracked or force-added.
- Install the checked-in `tools/evaluation/pre-push` as `.git/hooks/pre-push`
  in each Windows checkout, without overwriting an existing hook. It checks each
  outgoing ref against `refs/remotes/upstream/main` and fails closed on errors.
  It does not fetch or change refs. Check the configured hooks path first;
  combine checks manually if you already use hooks.
- Run `powershell.exe -NoProfile -File tools/evaluation/Test-PublicChanges.ps1`
  before pushing; add `-Staged` to check pending staged changes as well. The guard
  examines our additions, messages and commit identity, including earlier
  fork-only commits. It rejects common personal-path, private-IP, email, secret
  and private-artifact patterns without printing matched values. It also rejects
  binary additions pending manual review. Test it with `-SelfTest`.
- This is a precaution, not a complete confidentiality guarantee: review text,
  images and metadata manually. Inherited upstream content is outside this
  fork-specific check; hooks must be installed locally and can be bypassed.

Privacy-cleanup verification: the guard rejected the pre-cleanup history and
passed the sanitized candidate. Its synthetic self-tests and 15 configuration
validation cases passed under Windows PowerShell 5.1. Checkout and deployed
runtime `-CheckOnly` preflights passed with all four Retail DAT pairs verified;
launcher/profile hashes were unchanged. No client was launched by those checks.
The configuration cases can be rerun with
`powershell.exe -NoProfile -File tools/evaluation/Test-EvaluationConfig.ps1`.

The privacy rewrite removes the old evaluation commits from this development
branch's ancestry, not from every possible cached view, fork or clone. A private
local bundle preserves recovery. Do not merge the pre-cleanup development history
back into the cleaned branch or publish that bundle. Other checkouts must be
realigned before contributing. See GitHub's
[history cleanup limitations](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).
