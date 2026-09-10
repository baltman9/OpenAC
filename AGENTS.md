# Local OpenAC evaluation

- Work on `dev/openac-evaluation` or focused feature branches. Push ordinary
  updates there; do not push or merge to `main` without an approved substantial
  milestone. The default GitHub branch remains `main`.
- `origin` is `baltman9/OpenAC`; `upstream` is `eriknihlen/OpenAC`. Fetch upstream
  when resuming development. Integrate relevant upstream updates on the development
  branch with a fast-forward or normal merge, preserving our changes. Never
  force-push or automatically discard work without explicit approval for that
  operation. State the source commit tested.
- Follow CONTRIBUTING.md. Do not copy incompatible licensed code into this repo.
- The original `C:\Turbine\Asheron's Call` is read-only source material for this
  work. Preserve every existing Turbine test client and the shelved Vitaeum setup.
- Runtime test files belong in `C:\Games\OpenAC-Test`, not in Git. Never commit
  DATs, prepared packages, credentials, profile/session files, or private logs.
- Live tests target only the host in ignored `evaluation.local.json`, port 9010.
  Never publish that host. Never modify or test against the
  baseline port-9000 server. Do not reuse ACME memory hooks or debugger scripts.
- Do not launch gameplay, enter credentials, or perform server administration
  unless the current request explicitly includes it. Setup is not a live pass.
- Record results honestly and summarize completed work and the next test gate.
- This fork is public. Use `%USERPROFILE%` in Windows path examples (PowerShell:
  `$env:USERPROFILE`), never a literal personal home directory. Keep personal
  names/emails, LAN addresses, hostnames, credentials, private logs and unredacted
  screenshots out of tracked files and commit messages. Public GitHub handles and
  upstream attribution are intentional; do not rewrite upstream contributors.
- Machine-specific values belong only in ignored `evaluation.local.json` or
  runtime storage outside Git. Commit only the placeholder example configuration.
- Use the public GitHub handle and GitHub no-reply email for our commit identity.
  Run `tools/evaluation/Test-PublicChanges.ps1` before pushing and install the
  tracked pre-push hook as described in `docs/openac-evaluation.md`. Review the
  outgoing diff manually too: pattern checks cannot guarantee confidentiality.
