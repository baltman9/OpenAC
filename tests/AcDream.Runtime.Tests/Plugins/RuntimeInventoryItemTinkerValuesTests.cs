using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// A crafting calculator works out whether a tinker will take before it
/// spends the salvage, and every number that sum needs has to reach a plugin
/// off the one item snapshot the runtime projects. These pin each of those
/// numbers to the property the client actually holds it in, and pin the
/// reading an item that carries none of them gives back, because a
/// calculator that silently reads a stale default would spend the salvage
/// anyway.
/// </summary>
public sealed class RuntimeInventoryItemTinkerValuesTests
{
    private const uint ItemId = 0x70005001u;

    [Fact]
    public void TheSnapshotCarriesEveryCraftingValueTheItemHolds()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, staged =>
        {
            staged.Workmanship = 7.25f;
            staged.Structure = 83;
            staged.MaxStructure = 100;
            staged.ValidLocations = EquipMask.NeckWear;
            staged.Properties.Ints[(uint)PropertyInt.NumTimesTinkered] = 4;
            staged.Properties.Ints[(uint)PropertyInt.ImbuedEffect] = 0x0080;
            staged.Properties.Ints[(uint)PropertyInt.ArmorLevel] = 312;
            staged.Properties.Ints[(uint)PropertyInt.Damage] = 46;
            staged.Properties.Ints[(uint)PropertyInt.DamageType] = 0x40;
            staged.Properties.Floats[(uint)PropertyFloat.DamageVariance] = 0.42d;
            staged.Properties.Bools[(uint)PropertyBool.Retained] = true;
        });

        Assert.Equal(7.25d, item.SalvageWorkmanship, 4);
        Assert.Equal(4, item.NumTimesTinkered);
        Assert.Equal(0x0080, item.ImbuedEffect);
        Assert.Equal(312, item.ArmorLevel);
        Assert.Equal(46, item.MaxDamage);
        Assert.Equal(0x40, item.WandElementalDamageType);
        Assert.True(item.Retained);
        // The three a calculator reads under their existing names rather
        // than a second copy: the slot mask, the uses left, and the roll's
        // floor.
        Assert.Equal(0x8000u, item.ValidLocations);
        Assert.Equal(83, item.Structure);
        Assert.Equal(0.42d, item.DamageVariance, 4);
    }

    [Fact]
    public void AnItemThatCarriesNoneOfThemReadsInert()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static _ => { });

        Assert.Equal(0d, item.SalvageWorkmanship);
        Assert.Equal(0, item.NumTimesTinkered);
        Assert.Equal(0, item.ImbuedEffect);
        Assert.Equal(0, item.ArmorLevel);
        Assert.Equal(0, item.MaxDamage);
        Assert.Equal(0, item.WandElementalDamageType);
        Assert.False(item.Retained);
    }

    /// <summary>
    /// The server's "damage was never set" answer is a sentinel, not a
    /// number, and <c>Damage</c> passes it through as minus one. A
    /// calculator multiplying by that would come out negative, so
    /// <c>MaxDamage</c> folds it to zero while <c>Damage</c> keeps saying
    /// "unset".
    /// </summary>
    [Fact]
    public void MaxDamageFoldsTheServersUnsetDamageToZero()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static staged =>
            staged.WeaponProfile = new ClientWeaponProfile
            {
                Damage = uint.MaxValue,
            });

        Assert.Equal(-1, item.Damage);
        Assert.Equal(0, item.MaxDamage);
    }

    /// <summary>
    /// A wand's element lives in the item's own property table; the appraised
    /// weapon profile says what it strikes with in melee. The snapshot has to
    /// carry both, or a calculator picking salvage for a caster reads the
    /// wrong element.
    /// </summary>
    [Fact]
    public void TheWandElementSurvivesAnAppraisedWeaponProfile()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static staged =>
        {
            staged.Properties.Ints[(uint)PropertyInt.DamageType] = 0x40;
            staged.WeaponProfile = new ClientWeaponProfile
            {
                Damage = 12u,
                DamageType = 0x01u,
            };
        });

        Assert.Equal(0x01, item.DamageType);
        Assert.Equal(0x40, item.WandElementalDamageType);
        Assert.Equal(12, item.MaxDamage);
    }

    private static PluginInventoryItem CaptureOwned(
        NoWindowGameRuntimeHost host,
        Action<ClientObject> stage)
    {
        host.Start();
        for (int tick = 0; tick < 4; tick++)
            host.Session.Tick();
        GameRuntime runtime = host.Runtime;
        Assert.True(runtime.Session.IsInWorld);

        var staged = new ClientObject
        {
            ObjectId = ItemId,
            ContainerId = runtime.PlayerIdentity.ServerGuid,
            Name = "a staged item",
        };
        stage(staged);
        runtime.InventoryOwner.Objects.AddOrUpdate(staged);

        using var surface = new RuntimeAutomationSurface();
        RuntimeAutomationBindings.Apply(
            surface,
            runtime,
            new RuntimeAutomationHostCapabilities
            {
                HostName = "a host under test",
                Declared = RuntimeAutomationHostCapabilities.AllCapabilityNames,
            });

        return Assert.Single(
            surface.CaptureOwnedItems(),
            candidate => candidate.ObjectId == ItemId);
    }
}
