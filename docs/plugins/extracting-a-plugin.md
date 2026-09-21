# Extracting a plugin

A plugin that lives in this repository during its early life eventually moves
to its own. This is the checklist for that move: what the plugin has to own
before it can leave, and what it must not be leaning on. Doing these while the
plugin is still here is the point — each one can be proved by a build and a
test in this repository, where a mistake is cheap.

[Writing a plugin](../plugin-development.md) covers the same ground for a
plugin that starts outside; [the manifest contract](../plugin-manifest.md) has
the field-by-field rules.

## What the plugin has to own

- **Its own manifest.** A `plugin.json` checked in beside the project and
  copied to the build output, carrying the id it already uses (do not rename
  an id after release), a SemVer `version`, the `entryDll`, the `apiVersion`,
  and the `minHostVersion` and `hosts` a launcher install requires. Declare
  [capabilities](../plugin-manifest.md#capabilities) for anything a player
  would want to know about before installing.
- **Its own version.** While it is here the version comes from the
  repository's single version property, and the manifest has to be checked
  against the assembly so the two cannot drift. Once it leaves, the plugin's
  version is its own and moves on its own schedule; it is no longer tied to a
  client release.
- **A contract reference by version.** A `PackageReference` to
  `AcDream.Plugin.Abstractions` with `ExcludeAssets="runtime"`, not a project
  reference into a checkout. Check that the plugin builds that way before it
  leaves: nothing else will catch a dependency that only a checkout provides.
- **Its own tests.** A test project that references the plugin and nothing
  from the client, standing in for the host with its own fakes of the plugin
  API. Tests that need the client's own engine or its fixtures cannot travel;
  find them before the move and decide, one at a time, whether each becomes a
  fake, stays behind, or goes.
- **Its own content.** Panel markup, default profiles, anything the plugin
  reads at runtime, copied to the output directory and shipped in the zip.
- **A release the launcher can install.** Tag `v<version>` on a public
  repository, with assets named exactly `plugin.json`,
  `<id>-<version>.zip`, `<id>-<version>.zip.sha256`, and `icon.png` if the
  zip carries one. `plugin.json` sits at the zip root, not inside a folder.
  Run `acdream-plugincheck` against the zip first: it is the launcher's own
  code, so it cannot disagree with the install.
- **A listing, if you want one.** Open an issue on the plugin list repository
  named in [Writing a plugin](../plugin-development.md#getting-listed) with
  the plugin's id, display name, author, a one-line description and its
  repository. A plugin distributed by link installs perfectly well without a
  listing.

## What it must not depend on

- **Any client assembly but the contract.** Not the app, not the runtime, not
  the core. Pin this with a test over the built assembly's references while
  the plugin is still here, where a stray reference still compiles and so
  would otherwise go unnoticed.
- **Client internals.** An `InternalsVisibleTo` grant in either direction is a
  coupling that cannot survive the move. Grants the client's own test suites
  hold on the plugin count too, and they are the easy ones to forget.
- **Fixtures or helpers from the client's test projects.** Anything shared by
  file path across projects has to be copied into the plugin's repository or
  dropped.
- **The client's build rules.** A plugin folder staged into a host's output by
  the client's build is a convenience for developing here; once the plugin
  ships as a release, the launcher stages it, and the plugin's own build has to
  produce the whole folder on its own.
- **Native code, or a `runtimes/` folder.** Managed files only, by extension
  allowlist. This is refused at install time, not at build time, so it is
  easy to discover late.
- **Anything reached past the contract.** If the plugin needs something the
  contract does not expose, the change belongs in the contract, additively,
  before the move: [CONTRIBUTING.md](https://github.com/eriknihlen/OpenAC/blob/main/CONTRIBUTING.md)
  has the rules for an API change.

## Order that works

1. Give the plugin its manifest and a test that all three readers accept it:
   the client's, the launcher's install rules, and `acdream-plugincheck`.
2. Pin the contract boundary with a test, and clear whatever it catches.
3. Build the plugin against the contract package, not the checkout.
4. Move it, with its own test project, and cut a release.
5. Delete what stayed behind, including the build rules that staged it.