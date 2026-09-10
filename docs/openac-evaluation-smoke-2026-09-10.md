# OpenAC 0.1.1 Retail smoke-test review

## Outcome

The user reports successful portal travel, lifestone return, exit and re-entry.
Accept this as a **manual smoke-test pass for the installed configuration**, not
a complete stability pass, a plugin-free pass, or a pass of our development build.
Combat, prolonged load, higher-resolution asset overrides and optional render
packs have not been verified by this result.

## Read-only corroboration

- Installed active client and executable version: **0.1.1**.
- Both recorded sessions target the locally configured test host on port 9010
  and use the isolated Retail DAT folder and prepared package.
- First session: entered the world, disconnected, and exited with code **0**,
  reason **graceful**.
- Second session: entered the world and was still running at review;
  this review does not claim a second completed exit.
- Portal and lifestone success are user observations. The inspected lifecycle
  records do not independently identify those individual transitions.
- Both sessions explicitly report **`pluginLoaded: acdream.mosstank`**.
  Their plugin selection is null. Do not describe the run as plugin-free;
  a loaded plugin alone does not establish that automation was running.

Private profile/session documents and logs remain outside Git. No settings,
running processes, DATs, server state, or credentials were changed by review.

## Texture-quality correction

The user saw Medium landscape detail, Low environment detail, Trilinear
filtering, and Medium draw distance. Source inspection of the v0.1.1 config
controller (unchanged in our current branch) marks the first three as
`storeOnly: true`. Their properties appear in UI/settings persistence, but no
renderer consumer was found. Draw distance is a separate live setting.

Consequently, raising those dropdowns must not be presented as proof of a
higher-quality render or HD-asset test. The earlier suggestion to increase
these after stability testing needs this qualification. Source references:

- `src/AcDream.App/UI/Layout/ConfigOptionsPageController.cs`,
  `BindRenderingQualitySection`
- `src/AcDream.UI.Abstractions/Panels/Settings/DisplaySettings.cs`
- `src/AcDream.UI.Abstractions/Panels/Settings/SettingsStore.cs`

## Recommended next gate

Keep this successful release configuration as the comparison baseline. Next,
establish the .NET SDK/source-build and test baseline, and determine the supported
way to run without bundled plugins. Then review the actual texture-selection
and package-preparation paths before changing graphics or importing HD assets.
Do not require another long manual run merely to change store-only dropdowns.
The source-build/toolchain and controlled plugin baseline are a new work step.
Port 9000, all original clients, and `main` remain unchanged.
