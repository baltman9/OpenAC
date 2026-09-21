// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

/// <summary>
/// A settable <see cref="IAutomationSurface"/> for tests. Each area is a
/// settable property so the test can replace individual facets while
/// leaving the rest as defaults. By default every area delegates to
/// <see cref="NoOpAutomationSurface.Instance"/>.
/// </summary>
public sealed class FakeAutomationSurface : IAutomationSurface
{
    /// <summary>
    /// Controls the value returned by <see cref="IAutomationSurface.IsAvailable"/>.
    /// </summary>
    public bool IsAvailableValue { get; set; } = true;

    /// <summary>Character info area. Defaults to no-op.</summary>
    public ICharacterInfo Character { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Spell catalog area. Defaults to no-op.</summary>
    public ISpellCatalog Spells { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Magic commands area. Defaults to no-op.</summary>
    public IMagicCommands Magic { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Chat area. Defaults to no-op.</summary>
    public IPluginChat Chat { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Navigation area. Defaults to no-op.</summary>
    public INavigationAutomation Navigation { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Dialog automation area. Defaults to no-op.</summary>
    public IDialogAutomation Dialogs { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Combat area. Defaults to no-op.</summary>
    public ICombatAutomation Combat { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Equipment area. Defaults to no-op.</summary>
    public IEquipmentAutomation Equipment { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Item automation area. Defaults to no-op.</summary>
    public IItemAutomation Items { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Loot automation area. Defaults to no-op.</summary>
    public ILootAutomation Loot { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Fellowship area. Defaults to no-op.</summary>
    public IFellowshipAutomation Fellowship { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Enchantment area. Defaults to no-op.</summary>
    public IEnchantmentAutomation Enchantments { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>World object area. Defaults to no-op.</summary>
    public IWorldObjectAutomation Objects { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>World time area. Defaults to no-op.</summary>
    public IWorldTimeAutomation WorldTime { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Login automation area. Defaults to no-op.</summary>
    public ILoginAutomation Login { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Network automation area. Defaults to no-op.</summary>
    public INetworkAutomation Network { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Recovery automation area. Defaults to no-op.</summary>
    public IRecoveryAutomation Recovery { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Projectile automation area. Defaults to no-op.</summary>
    public IProjectileAutomation Projectiles { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Selection automation area. Defaults to no-op.</summary>
    public ISelectionAutomation Selection { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Trade automation area. Defaults to no-op.</summary>
    public ITradeAutomation Trade { get; set; } = NoOpAutomationSurface.Instance;

    /// <summary>Vendor automation area. Defaults to no-op.</summary>
    public IVendorAutomation Vendor { get; set; } = NoOpAutomationSurface.Instance;

    /// <inheritdoc/>
    public bool IsAvailable => IsAvailableValue;
}