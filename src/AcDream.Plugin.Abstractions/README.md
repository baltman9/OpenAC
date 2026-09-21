# OpenAC plugin API

This package is the contract an [OpenAC](https://github.com/eriknihlen/OpenAC)
plugin is written against. A plugin references this package and nothing else
from the client: that is what lets one build run both in the graphical client
and in the windowless host, and lets the client change underneath without
breaking the plugin.

Implement `IAcDreamPlugin` on one public class with a public parameterless
constructor. The host constructs it, calls `Initialize(IPluginHost host)` once,
then `Enable()` and `Disable()`. Everything a plugin can see or do is reached
through `IPluginHost`: `State`, `Events`, `Commands`, `Storage`, `Log`, `Ui`,
`Window`, `Clipboard`, `Hotkeys`, `WorldLines` and `Automation`.

Changes to the contract are additive: new capabilities arrive as new members
with default implementations, so a plugin built against an older version keeps
compiling and keeps loading. A member a particular host cannot provide returns
`false`, `Unavailable` or an empty value rather than throwing, so check
`IsAvailable` on a surface before relying on it.

Reference it compile-only. The host already ships the contract, and a second
copy beside the plugin would hand it types the host cannot accept:

```xml
<PackageReference Include="AcDream.Plugin.Abstractions" Version="0.1.12"
                  ExcludeAssets="runtime" />
```

Guides, the full API reference and the manifest a plugin folder needs are at
<https://eriknihlen.github.io/OpenAC/>.