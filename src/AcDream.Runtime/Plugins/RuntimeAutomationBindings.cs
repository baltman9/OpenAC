using System.Reflection;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Navigation;

namespace AcDream.Runtime.Plugins;

/// <summary>The eight item commands every host can answer.</summary>
internal sealed record RuntimeAutomationItemCommands(
    Func<uint, bool> Use,
    Func<uint, uint, bool> Apply,
    Func<uint, uint, uint, int, bool> Move,
    Func<uint, uint, uint, bool> Merge,
    Func<uint, uint, bool> Drop,
    Func<uint, uint, uint, bool> Give,
    Func<uint, bool, bool> Pickup,
    Func<uint, bool> Identify);

/// <summary>Wielding, and whether a wield is already in flight.</summary>
internal sealed record RuntimeAutomationEquipmentCommands(
    Func<uint, uint, bool> Equip,
    Func<bool> IsBusy,
    Func<uint, bool> EquipSecondary);

/// <summary>Leaving the world, and whether that is allowed right now.</summary>
internal sealed record RuntimeAutomationLogoutCommands(
    Func<bool> Request,
    Func<bool> CanRequest);

/// <summary>
/// What a particular host lends the plugin surface. Everything a runtime
/// owner can answer on its own is bound inside
/// <see cref="RuntimeAutomationBindings.Apply"/> and does not appear here;
/// what is left is either genuinely host-shaped (a keyboard going into a
/// chat entry, a window's chat draft) or an operation that has not been
/// moved into the runtime yet, in which case the host that lacks it carries
/// a seam-census allow-list row saying so.
/// </summary>
/// <remarks>
/// <see cref="Declared"/> is the host's standing claim about which of these
/// it can ever supply; the census compares those claims between hosts
/// without needing either host to be running. <see cref="Apply"/> refuses a
/// record that supplies something its host never declared, so the claim
/// cannot drift away from the code that builds the record.
/// </remarks>
internal sealed record RuntimeAutomationHostCapabilities
{
    /// <summary>The host this record was built by, for failure messages.</summary>
    public required string HostName { get; init; }

    /// <summary>Every capability name this host can ever supply.</summary>
    public required IReadOnlySet<string> Declared { get; init; }

    /// <summary>
    /// Capabilities this host declares but can only supply when a condition
    /// holds, keyed by capability name with the condition in plain terms
    /// (for example "only with installed content"). Every key must also be
    /// in <see cref="Declared"/>. A capability outside this map is one the
    /// host claims it always supplies, so a missing one is a defect rather
    /// than a configuration.
    /// </summary>
    public IReadOnlyDictionary<string, string> Conditional { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Where a binding problem is reported; host-shaped, not a capability.</summary>
    public Action<string>? Warn { get; init; }

    /// <summary>
    /// Whether the keyboard is going into text rather than into the character.
    /// Host-shaped, not a capability: the chat entry is a runtime owner, and
    /// this is the one thing about it only a host with a keyboard can know. A
    /// host without one leaves it out and the entry decides for itself.
    /// </summary>
    public Func<bool>? KeyboardGoesToText { get; init; }

    public IDatReaderWriter? Content { get; init; }
    public MagicCatalog? MagicCatalog { get; init; }
    public Func<string, bool>? SubmitChatText { get; init; }
    public IGameRuntimeCommands? SessionCommands { get; init; }
    public NavigationWalkController? NavigationWalk { get; init; }
    public RuntimeAutomationEquipmentCommands? Equipment { get; init; }
    public RuntimeAutomationItemCommands? Items { get; init; }
    public Func<uint, IReadOnlyList<uint>, bool>? SalvageItems { get; init; }
    public Func<uint, uint, int, bool>? SellItem { get; init; }
    public RuntimeAutomationLogoutCommands? Logout { get; init; }
    public Func<uint, bool, bool>? AnswerConfirmation { get; init; }
    public Func<uint, PluginItemCommandResult>? UseWorldObject { get; init; }
    public Func<uint, bool>? DismissGhost { get; init; }
    public Func<PluginSelectionAction, bool>? SelectionAction { get; init; }
    public Func<int, string>? SpeciesName { get; init; }
    public PhysicsEngine? ProjectileCollision { get; init; }
    public bool RemoteBodiesUnsimulated { get; init; }

    /// <summary>The names of the properties that are not the host's own bookkeeping.</summary>
    internal static IReadOnlyList<PropertyInfo> CapabilityProperties { get; } =
        typeof(RuntimeAutomationHostCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.Name is not (
                nameof(HostName) or nameof(Declared) or nameof(Warn)
                or nameof(Conditional) or nameof(KeyboardGoesToText)))
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every capability name the surface knows how to be given.</summary>
    internal static IReadOnlySet<string> AllCapabilityNames { get; } =
        CapabilityProperties
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>What this particular record actually carries.</summary>
    internal IReadOnlySet<string> Supplied()
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in CapabilityProperties)
        {
            object? value = property.GetValue(this);
            if (value is bool flag ? flag : value is not null)
                supplied.Add(property.Name);
        }
        return supplied;
    }
}

/// <summary>
/// The one place the plugin surface's seams are filled. Both hosts call it,
/// so a seam that exists for one and not the other is a difference in the
/// capability record rather than in two separately written wiring blocks
/// nothing compares.
/// </summary>
internal static class RuntimeAutomationBindings
{
    /// <summary>The retail skill table every host reads skill names and icons from.</summary>
    private const uint SkillTableId = 0x0E000004u;

    /// <summary>
    /// Which capability each seam needs, or <see langword="null"/> when the
    /// seam is filled from the runtime itself and therefore exists on every
    /// host. A key of the form <c>Method.parameter</c> is an optional
    /// argument of that seam, which a host can leave out on its own.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> SeamCapabilities { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Bind"] = null,
            ["BindSkillNames"] = nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindSkillIcons"] = nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindPaletteColorResolver"] =
                nameof(RuntimeAutomationHostCapabilities.Content),
            ["BindMagicCatalog"] =
                nameof(RuntimeAutomationHostCapabilities.MagicCatalog),
            ["BindSubmit"] =
                nameof(RuntimeAutomationHostCapabilities.SubmitChatText),
            ["BindSessionCommands"] =
                nameof(RuntimeAutomationHostCapabilities.SessionCommands),
            ["BindNavigationWalk"] =
                nameof(RuntimeAutomationHostCapabilities.NavigationWalk),
            ["BindEquipment"] =
                nameof(RuntimeAutomationHostCapabilities.Equipment),
            ["BindEquipment.equipSecondary"] =
                nameof(RuntimeAutomationHostCapabilities.Equipment),
            ["BindItems"] = nameof(RuntimeAutomationHostCapabilities.Items),
            ["BindItems.salvageItems"] =
                nameof(RuntimeAutomationHostCapabilities.SalvageItems),
            ["BindItems.sellItem"] =
                nameof(RuntimeAutomationHostCapabilities.SellItem),
            ["BindLogout"] = nameof(RuntimeAutomationHostCapabilities.Logout),
            ["BindDialogs"] =
                nameof(RuntimeAutomationHostCapabilities.AnswerConfirmation),
            ["BindWorldObjectUse"] =
                nameof(RuntimeAutomationHostCapabilities.UseWorldObject),
            ["BindGhostDeletion"] =
                nameof(RuntimeAutomationHostCapabilities.DismissGhost),
            ["BindSelectionActions"] =
                nameof(RuntimeAutomationHostCapabilities.SelectionAction),
            ["BindChatInputActive"] = null,
            ["BindChatComposer"] = null,
            ["BindSpeciesNameResolver"] =
                nameof(RuntimeAutomationHostCapabilities.SpeciesName),
            ["BindProjectileCollision"] =
                nameof(RuntimeAutomationHostCapabilities.ProjectileCollision),
            ["BindRemoteBodiesUnsimulated"] =
                nameof(RuntimeAutomationHostCapabilities.RemoteBodiesUnsimulated),
        };

    /// <summary>
    /// Fills every seam the runtime can answer and every seam the host has
    /// lent a capability for, and reports which seams that came to. The
    /// report is what a test compares against
    /// <see cref="SeamCapabilities"/>, so the map cannot claim a seam the
    /// code does not actually bind.
    /// </summary>
    internal static IReadOnlySet<string> Apply(
        RuntimeAutomationSurface surface,
        GameRuntime runtime,
        RuntimeAutomationHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(capabilities);

        IReadOnlySet<string> supplied = capabilities.Supplied();
        string[] undeclared = supplied
            .Where(name => !capabilities.Declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (undeclared.Length != 0)
        {
            throw new InvalidOperationException(
                $"The {capabilities.HostName} host supplied plugin "
                + $"capabilities it does not declare: "
                + $"{string.Join(", ", undeclared)}. Add them to the host's "
                + "declared set so the host-parity census can see them.");
        }

        string[] conditionalButUndeclared = capabilities.Conditional.Keys
            .Where(name => !capabilities.Declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (conditionalButUndeclared.Length != 0)
        {
            throw new InvalidOperationException(
                $"The {capabilities.HostName} host names conditions for "
                + "plugin capabilities it does not declare at all: "
                + $"{string.Join(", ", conditionalButUndeclared)}.");
        }

        var bound = new HashSet<string>(StringComparer.Ordinal);

        if (!surface.EnsureBound(
            runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast))
        {
            capabilities.Warn?.Invoke(
                "plugin automation: the surface is already shut down, so "
                + "nothing was bound for this session");
            return bound;
        }
        bound.Add("Bind");

        if (capabilities.Content is { } content)
            BindContent(surface, content, capabilities.Warn, bound);
        if (capabilities.MagicCatalog is { } magicCatalog)
        {
            surface.BindMagicCatalog(magicCatalog);
            bound.Add(nameof(surface.BindMagicCatalog));
        }
        if (capabilities.SubmitChatText is { } submitChatText)
        {
            surface.BindSubmit(submitChatText);
            bound.Add("BindSubmit");
        }
        if (capabilities.SessionCommands is { } sessionCommands)
        {
            surface.BindSessionCommands(sessionCommands);
            bound.Add(nameof(surface.BindSessionCommands));
        }
        if (capabilities.NavigationWalk is { } navigationWalk)
        {
            surface.BindNavigationWalk(navigationWalk);
            bound.Add(nameof(surface.BindNavigationWalk));
        }
        if (capabilities.Equipment is { } equipment)
        {
            surface.BindEquipment(
                equipment.Equip, equipment.IsBusy, equipment.EquipSecondary);
            bound.Add(nameof(surface.BindEquipment));
            bound.Add("BindEquipment.equipSecondary");
        }
        if (capabilities.Items is { } items)
        {
            surface.BindItems(
                items.Use,
                items.Apply,
                items.Move,
                items.Merge,
                items.Drop,
                items.Give,
                items.Pickup,
                items.Identify,
                capabilities.SalvageItems,
                capabilities.SellItem);
            bound.Add(nameof(surface.BindItems));
            if (capabilities.SalvageItems is not null)
                bound.Add("BindItems.salvageItems");
            if (capabilities.SellItem is not null)
                bound.Add("BindItems.sellItem");
        }
        if (capabilities.Logout is { } logout)
        {
            surface.BindLogout(logout.Request, logout.CanRequest);
            bound.Add(nameof(surface.BindLogout));
        }
        if (capabilities.AnswerConfirmation is { } answerConfirmation)
        {
            surface.BindDialogs(answerConfirmation);
            bound.Add(nameof(surface.BindDialogs));
        }
        if (capabilities.UseWorldObject is { } useWorldObject)
        {
            surface.BindWorldObjectUse(useWorldObject);
            bound.Add(nameof(surface.BindWorldObjectUse));
        }
        if (capabilities.DismissGhost is { } dismissGhost)
        {
            surface.BindGhostDeletion(dismissGhost);
            bound.Add(nameof(surface.BindGhostDeletion));
        }
        if (capabilities.SelectionAction is { } selectionAction)
        {
            surface.BindSelectionActions(selectionAction);
            bound.Add(nameof(surface.BindSelectionActions));
        }
        // The chat entry is a runtime owner, so both hosts answer these from
        // the same place: a console front end and a chat box are two ways of
        // driving one entry.
        AcDream.Runtime.Chat.RuntimeChatEntryOwner chatEntry =
            runtime.CommunicationOwner.ChatEntryOwner;
        chatEntry.BindInputActiveSource(capabilities.KeyboardGoesToText);
        surface.BindChatInputActive(() => chatEntry.IsInputActive);
        bound.Add(nameof(surface.BindChatInputActive));
        surface.BindChatComposer(chatEntry.Compose);
        bound.Add(nameof(surface.BindChatComposer));
        if (capabilities.SpeciesName is { } speciesName)
        {
            surface.BindSpeciesNameResolver(speciesName);
            bound.Add(nameof(surface.BindSpeciesNameResolver));
        }
        if (capabilities.ProjectileCollision is { } projectilePhysics)
        {
            surface.BindProjectileCollision(projectilePhysics);
            bound.Add(nameof(surface.BindProjectileCollision));
        }
        if (capabilities.RemoteBodiesUnsimulated)
        {
            surface.BindRemoteBodiesUnsimulated();
            bound.Add("BindRemoteBodiesUnsimulated");
        }

        ReportDeclaredButUnfilled(capabilities, bound);
        return bound;
    }

    /// <summary>
    /// Says which seams the host's declaration promised and this run did not
    /// fill. A host guards every capability with a nullable part, so a part
    /// that came back null quietly leaves a seam empty while the parity
    /// census -- which reads declarations, not runs -- still swears it is
    /// filled. A capability whose condition the host named is reported as a
    /// configuration; anything else is reported as a defect.
    /// </summary>
    private static void ReportDeclaredButUnfilled(
        RuntimeAutomationHostCapabilities capabilities,
        IReadOnlySet<string> bound)
    {
        if (capabilities.Warn is not { } warn)
            return;

        foreach (string seam in SeamCapabilities
            .Where(entry => entry.Value is { } capability
                && capabilities.Declared.Contains(capability)
                && !bound.Contains(entry.Key))
            .Select(static entry => entry.Key)
            .OrderBy(static seam => seam, StringComparer.Ordinal))
        {
            string capability = SeamCapabilities[seam]!;
            warn(capabilities.Conditional.TryGetValue(
                capability, out string? condition)
                ? $"plugin automation: {seam} is unfilled on the "
                    + $"{capabilities.HostName} host because {condition}; "
                    + "plugins asking for it get nothing this session"
                : $"plugin automation: {seam} is unfilled on the "
                    + $"{capabilities.HostName} host although the host "
                    + "declares it unconditionally; a plugin asking for it "
                    + "gets nothing");
        }
    }

    /// <summary>
    /// What the installed data files lend the surface: palette colours for
    /// appearance, and the skill table, without which a plugin sees the
    /// character's skills unnamed and cannot judge what it can cast. One
    /// reader, one load, the same result on either host.
    /// </summary>
    private static void BindContent(
        RuntimeAutomationSurface surface,
        IDatReaderWriter content,
        Action<string>? warn,
        HashSet<string> bound)
    {
        surface.BindPaletteColorResolver(
            new AcDream.Content.CharGen.ChargenAppearanceCatalog(content));
        bound.Add(nameof(surface.BindPaletteColorResolver));

        if (!content.TryGet<DatReaderWriter.DBObjs.SkillTable>(
                SkillTableId, out var skillTable)
            || skillTable is null)
        {
            warn?.Invoke(
                "plugin automation: the retail skill table is missing, so "
                + "plugins will see unnamed skills");
            return;
        }

        var names = new Dictionary<uint, string>(skillTable.Skills.Count);
        var icons = new Dictionary<uint, uint>(skillTable.Skills.Count);
        foreach (var entry in skillTable.Skills)
        {
            names[(uint)entry.Key] = entry.Value.Name;
            icons[(uint)entry.Key] = entry.Value.IconId;
        }
        surface.BindSkillNames(names);
        surface.BindSkillIcons(icons);
        bound.Add(nameof(surface.BindSkillNames));
        bound.Add(nameof(surface.BindSkillIcons));
    }
}
