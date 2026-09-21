using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;
using System.Numerics;

namespace AcDream.Runtime.Tests.Plugins;

public sealed class CampaignAutomationSurfaceTests
{
    [Fact]
    public void ProjectileDebugSamplesAreDetachedValidatedAndClearedOnUnbind()
    {
        using var surface = new RuntimeAutomationSurface();
        PluginProjectileDebugSample[] source =
        [
            new(new Vector3(1f, 2f, 3f), true, 0.4f),
            new(new Vector3(float.NaN, 0f, 0f), false, 0.4f),
        ];

        surface.Projectiles.ShowDebugSamples(source);
        source[0] = default;

        PluginProjectileDebugSample sample = Assert.Single(
            surface.CaptureProjectileDebugSamples());
        Assert.Equal(new Vector3(1f, 2f, 3f), sample.WorldPosition);
        Assert.True(sample.IsClear);

        surface.Unbind();
        Assert.Empty(surface.CaptureProjectileDebugSamples());
    }

    /// <summary>
    /// The single-property read answers the same "no" the whole-bundle read
    /// answers when there is no session to ask, rather than reaching through
    /// a null runtime. Mutation: dropping the runtime/availability guard
    /// throws a NullReferenceException here instead of answering false.
    /// </summary>
    [Fact]
    public void SinglePropertyReadAgreesWithTheBundleReadWhileUnbound()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.False(surface.Objects.TryCaptureProperties(0x50000001u, out _));
        Assert.False(surface.Objects.TryGetIntProperty(
            0x50000001u,
            131u,
            out int material));
        Assert.Equal(0, material);
    }

    [Fact]
    public void SelectionAutomationUsesTheBoundCanonicalActionRoute()
    {
        using var surface = new RuntimeAutomationSurface();
        var actions = new List<PluginSelectionAction>();
        surface.BindSelectionActions(action =>
        {
            actions.Add(action);
            return true;
        });

        Assert.True(surface.Selection.Execute(
            PluginSelectionAction.PreviousSelection));
        Assert.True(surface.Selection.Execute(
            PluginSelectionAction.NextPlayer));
        Assert.Equal(
            [
                PluginSelectionAction.PreviousSelection,
                PluginSelectionAction.NextPlayer,
            ],
            actions);
    }

    [Theory]
    [InlineData((uint)ItemType.MeleeWeapon, 0u, PluginObjectClass.MeleeWeapon)]
    [InlineData((uint)ItemType.Armor, 0u, PluginObjectClass.Armor)]
    [InlineData((uint)ItemType.Creature, 0x10u, PluginObjectClass.Monster)]
    [InlineData((uint)ItemType.Creature, 0u, PluginObjectClass.Npc)]
    [InlineData((uint)ItemType.Creature, 0x04000010u, PluginObjectClass.CombatPet)]
    [InlineData((uint)ItemType.Creature, 0x8u, PluginObjectClass.Player)]
    [InlineData((uint)ItemType.Misc, 0x200u, PluginObjectClass.Vendor)]
    [InlineData((uint)ItemType.Misc, 0x1000u, PluginObjectClass.Door)]
    public void ObjectClassProjectionMatchesVirindiPriority(
        uint itemType,
        uint publicFlags,
        PluginObjectClass expected)
    {
        var item = new ClientObject
        {
            ObjectId = 1u,
            Type = (ItemType)itemType,
            PublicWeenieBitfield = publicFlags,
        };

        Assert.Equal(expected, RuntimeAutomationSurface.ClassifyObject(item));
    }

    [Fact]
    public void NavigationProjectionUsesVtankMapCoordinatesAndCompassHeading()
    {
        PluginNavigationPosition center =
            RuntimeAutomationSurface.ProjectNavigationPosition(new Position(
                0x7F7F0001u,
                new Vector3(84f, 84f, 240f),
                Quaternion.Identity));

        Assert.Equal(0d, center.EastWest, 8);
        Assert.Equal(0d, center.NorthSouth, 8);
        Assert.Equal(1d, center.Elevation, 8);
        Assert.Equal(0f, center.HeadingDegrees, 4);
        Assert.True(center.IsOutdoor);

        PluginNavigationPosition nextBlock =
            RuntimeAutomationSurface.ProjectNavigationPosition(new Position(
                0x80800041u,
                new Vector3(84f, 84f, 0f),
                Quaternion.Identity));

        Assert.Equal(0.8d, nextBlock.EastWest, 8);
        Assert.Equal(0.8d, nextBlock.NorthSouth, 8);
        Assert.False(nextBlock.IsOutdoor);
    }

    /// <summary>
    /// The equipment projection's order is part of the contract clients read
    /// it by: what is held comes first, then name, then object id. "The first
    /// profiled wand" means the one in hand when there is one.
    /// Mutation: drop the equipped term from the sort and the wielded wand
    /// stops leading.
    /// </summary>
    [Fact]
    public void EquipmentIsProjectedEquippedFirstThenByNameThenById()
    {
        List<PluginEquipmentItem> items =
        [
            Wand(0x70000003u, "Alpha Wand"),
            Wand(0x70000002u, "Alpha Wand"),
            Wand(0x70000001u, "Zeta Wand", equipped: true),
        ];

        items.Sort(RuntimeAutomationSurface.CompareEquipmentOrder);

        Assert.Equal(
            [0x70000001u, 0x70000002u, 0x70000003u],
            items.Select(static item => item.ObjectId));
        Assert.True(items[0].IsEquipped);
    }

    /// <summary>
    /// The order is a contract of the projection, not of the comparator that
    /// happens to implement it, so it is read off the list the projection
    /// builds rather than off a list the test sorted itself.
    /// Mutation: delete the sort at the end of <c>BuildOwnedEquipment</c> and
    /// this fails while the comparator pin above stays green.
    /// </summary>
    [Fact]
    public void TheEquipmentProjectionHandsBackThatOrder()
    {
        const uint player = 0x50000001u;
        var objects = new ClientObjectTable();
        // The held one has the HIGHEST id and the last name, so neither the
        // table's own order nor any single term can produce this answer by
        // accident.
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000003u,
            Name = "Alpha Wand",
            ValidLocations = EquipMask.Held,
            ContainerId = player,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000002u,
            Name = "Alpha Wand",
            ValidLocations = EquipMask.Held,
            ContainerId = player,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x70000009u,
            Name = "Zeta Wand",
            ValidLocations = EquipMask.Held,
            CurrentlyEquippedLocation = EquipMask.Held,
            WielderId = player,
        });

        List<PluginEquipmentItem> projected =
            RuntimeAutomationSurface.BuildOwnedEquipment(objects, player);

        Assert.Equal(
            [0x70000009u, 0x70000002u, 0x70000003u],
            projected.Select(static item => item.ObjectId));
        Assert.True(projected[0].IsEquipped);
    }

    /// <summary>
    /// A plugin has to be able to tell the character's own combat log from
    /// somebody typing the same words, so the line's log-text type reaches it.
    /// Mutation: stop copying it in <c>OnChat</c> and this fails.
    /// </summary>
    [Fact]
    public void ChatCapture_CarriesTheLinesLogTextType()
    {
        using GameRuntime runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(
            runtime,
            runtime.CharacterOwner,
            runtime.ActionOwner.SpellCast);

        runtime.CommunicationOwner.AddText(
            "You cast Imperil Other VII on Olthoi.",
            RetailLogTextType.Magic);

        PluginChatMessage message = Assert.Single(surface.CaptureMessages(0));
        Assert.Equal((int)RetailLogTextType.Magic, message.LogTextType);
    }

    private static PluginEquipmentItem Wand(
        uint objectId,
        string name,
        bool equipped = false) => new(
        objectId,
        name,
        ItemType: 0x8000u,
        ValidLocations: 0x01000000u,
        EquippedLocation: equipped ? 0x01000000u : 0u,
        ContainerObjectId: equipped ? 0u : 1u,
        WielderObjectId: equipped ? 1u : 0u,
        CombatUse: 1,
        DamageType: 0,
        WeaponSkill: 0,
        Damage: 0,
        DamageVariance: 0d);

    [Fact]
    public void ChatCapture_isOrderedCursorBasedAndDetachesAcrossSessions()
    {
        using var first = GameRuntimeTestFactory.Create();
        using var second = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        int before = second.CommunicationOwner.SubscriberCount;
        surface.Bind(
            first,
            first.CharacterOwner,
            first.ActionOwner.SpellCast);

        first.CommunicationOwner.AddText(
            "You cast Imperil Other VII on Olthoi.",
            RetailLogTextType.Magic);
        PluginChatMessage one = Assert.Single(surface.CaptureMessages(0));
        Assert.Equal("You cast Imperil Other VII on Olthoi.", one.Text);
        Assert.Empty(surface.CaptureMessages(one.Sequence));

        surface.Bind(
            second,
            second.CharacterOwner,
            second.ActionOwner.SpellCast);
        first.CommunicationOwner.AddText(
            "stale first-session line",
            RetailLogTextType.Magic);
        second.CommunicationOwner.AddText(
            "You cast Fester Other VII on Olthoi.",
            RetailLogTextType.Magic);

        PluginChatMessage two = Assert.Single(
            surface.CaptureMessages(one.Sequence));
        Assert.True(two.Sequence > one.Sequence);
        Assert.Equal("You cast Fester Other VII on Olthoi.", two.Text);
        Assert.True(second.CommunicationOwner.SubscriberCount > before);

        surface.Dispose();
        Assert.Equal(before, second.CommunicationOwner.SubscriberCount);
    }

    [Fact]
    public void PostSystemMessage_RoutesToChatLog_NeverSpewBox()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.PostSystemMessage("MossTank: buffs applied.");

        var entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("MossTank: buffs applied.", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        runtime.CommunicationOwner.SpewBox.Tick(0d);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void InventoryCompletionProjectsTheCanonicalRequestReceipt()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        const uint itemId = 0x50000123u;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "Stack",
            StackSize = 10,
            StackSizeMax = 100,
        });

        Assert.True(runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Merge,
            itemId,
            static () => true));
        Assert.True(objects.UpdateStackSize(itemId, 9, 0));

        PluginInventoryCompletion completion =
            surface.Items.LastInventoryCompletion;
        Assert.True(completion.Revision > 0);
        Assert.Equal(PluginInventoryCommandKind.Merge, completion.Kind);
        Assert.Equal(itemId, completion.SourceObjectId);
        Assert.True(completion.IsSuccess);
    }

    [Fact]
    public void RecoveryClearsExactlyOneCanonicalBusyReference()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.InventoryOwner.Transactions.IncrementBusyCount();
        runtime.InventoryOwner.Transactions.IncrementBusyCount();

        PluginRecoveryResult first = surface.Recovery.ClearOneBusyReference();
        PluginRecoveryResult second = surface.Recovery.ClearOneBusyReference();
        PluginRecoveryResult alreadyClear =
            surface.Recovery.ClearOneBusyReference();

        Assert.True(first.Accepted);
        Assert.Equal((2, 1), (first.PreviousCount, first.CurrentCount));
        Assert.Equal((1, 0), (second.PreviousCount, second.CurrentCount));
        Assert.Equal((0, 0),
            (alreadyClear.PreviousCount, alreadyClear.CurrentCount));
        Assert.Equal(0, runtime.InventoryOwner.Transactions.BusyCount);
    }

    [Fact]
    public void EnchantmentLedgerSharesReportedAndConfirmedLocalDurationCasts()
    {
        var operations = new SpellOperations();
        using var runtime = GameRuntimeTestFactory.Create(spellCast: operations);
        runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create([DurationSpell()]));
        runtime.CharacterOwner.Spellbook.OnSpellLearned(42u);
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.True(surface.Enchantments.ReportCast(100u, 42u, 30d));
        PluginTrackedEnchantment reported = Assert.Single(
            surface.Enchantments.Capture(100u));
        Assert.Equal(7u, reported.Family);
        Assert.Equal(350, reported.Quality);
        Assert.InRange(reported.SecondsRemaining, 29d, 30d);

        runtime.ActionOwner.Selection.Select(
            200u,
            SelectionChangeSource.Plugin);
        Assert.Equal(
            CastRequestResult.Sent,
            runtime.ActionOwner.SpellCast.Cast(42u));
        Assert.True(runtime.ActionOwner.SpellCast.CompleteUse(0u));

        Assert.True(surface.Magic.LastCompletion.IsSuccess);
        PluginTrackedEnchantment local = Assert.Single(
            surface.Enchantments.Capture(200u));
        Assert.Equal(42u, local.SpellId);
        Assert.InRange(local.SecondsRemaining, 59d, 60d);

        surface.Unbind();
        Assert.Empty(surface.Enchantments.Capture(100u));
        Assert.Empty(surface.Enchantments.Capture(200u));
    }

    private static SpellMetadata DurationSpell() => new(
        42u,
        "Fire Vulnerability Other VII",
        "Life Magic",
        7u,
        0u,
        string.Empty,
        60f,
        10,
        true,
        false,
        string.Empty,
        0,
        350,
        0u,
        7,
        false,
        true,
        false,
        0f,
        0u,
        0u,
        1u,
        0);

    private sealed class SpellOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 1u;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    [Fact]
    public void ProjectWorldObject_FillsIconIdFromTheClientObject()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        var item = new ClientObject
        {
            ObjectId = 0x50000456u,
            Name = "Test Item",
            IconId = 0x06000165u,
        };

        System.Reflection.MethodInfo method =
            typeof(AcDream.Runtime.Plugins.RuntimeAutomationSurface)
            .GetMethod(
                "ProjectWorldObject",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "RuntimeAutomationSurface.ProjectWorldObject was not found by reflection.");
        var result = (PluginWorldObject)method.Invoke(
            surface,
            [runtime, null, item, 0u])!;

        Assert.Equal(0x06000165u, result.IconId);
    }



    /// <summary>
    /// The inert surface every host without a live local player falls back
    /// to — including <c>HeadlessAutomationSurface</c>, which has no
    /// navigation surface of its own — must refuse rather than pretend.
    /// </summary>


    /// <summary>
    /// One plugin, two hosts: the graphical host answers a plugin out of the
    /// shared presentation-free surface and adds nothing of its own to it.
    /// If the graphical host ever grew a private answer to an automation
    /// question, a plugin would behave differently here than in the
    /// windowless host — that is the divergence this pins shut. The
    /// windowless host has the mirror of this test.
    /// </summary>

    [Fact]
    public void OwnedItemProjectionCarriesTheSingularNameForAStack()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        var stack = new ClientObject
        {
            ObjectId = 0x50000777u,
            Name = "Lead Scarab",
            PluralName = "Lead Scarabs",
            StackSize = 5,
            StackSizeMax = 100,
        };

        PluginInventoryItem item = surface.ProjectInventoryItem(runtime, stack);

        Assert.Equal("Lead Scarabs", stack.GetAppropriateName());
        Assert.Equal("Lead Scarab", item.Name);
        Assert.Equal(5, item.StackSize);
    }
}
