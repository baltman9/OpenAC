using AcDream.Content;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using DatReaderWriter.DBObjs;

namespace AcDream.Headless.Plugins;

/// <summary>
/// The windowless host's plugin automation surface. It is the shared runtime
/// surface, bound to this host's runtime and command route, so a plugin sees
/// exactly the client the graphical host shows it. All this adds is the
/// windowless host's own wiring.
/// </summary>
internal sealed class HeadlessAutomationSurface : RuntimeAutomationSurface
{
    /// <summary>The retail skill table every host reads skill names from.</summary>
    private const uint SkillTableId = 0x0E000004u;

    internal HeadlessAutomationSurface(
        GameRuntime runtime,
        IEvents? events = null,
        IGameRuntimeCommands? commands = null,
        Func<string, bool>? submitChatText = null,
        IDatReaderWriter? content = null,
        MagicCatalog? magicCatalog = null,
        Action<string>? warn = null)
        : base(events)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        if (commands is not null && submitChatText is not null)
        {
            BindSessionCommands(new SessionCommandSeam(
                commands,
                () => runtime.Generation,
                submitChatText));
        }

        if (magicCatalog is not null)
        {
            BindMagicCatalog(magicCatalog);
            // Which weenies are component packs is named by the installed
            // data files, so it reaches the item owner from whichever host
            // loaded them.
            runtime.ItemInteractionOwner.BindComponentPackResolver(
                magicCatalog.IsComponentPack);
        }
        if (content is null)
            return;

        BindPaletteColorResolver(
            new AcDream.Content.CharGen.ChargenAppearanceCatalog(content));
        if (!content.TryGet<SkillTable>(SkillTableId, out SkillTable? skillTable)
            || skillTable is null)
        {
            warn?.Invoke(
                "plugin automation: the retail skill table is missing, so "
                + "plugins will see unnamed skills");
            return;
        }

        var names = new Dictionary<uint, string>(skillTable.Skills.Count);
        var icons = new Dictionary<uint, uint>(skillTable.Skills.Count);
        foreach (KeyValuePair<DatReaderWriter.Enums.SkillId, DatReaderWriter.Types.SkillBase>
            entry in skillTable.Skills)
        {
            names[(uint)entry.Key] = entry.Value.Name;
            icons[(uint)entry.Key] = entry.Value.IconId;
        }
        BindSkillNames(names);
        BindSkillIcons(icons);
    }
}
