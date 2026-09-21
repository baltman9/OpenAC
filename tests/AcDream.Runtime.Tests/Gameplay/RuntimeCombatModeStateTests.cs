using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCombatModeStateTests
{
    /// <summary>
    /// A change asked for while the body is still finishing a motion is not
    /// sent: it is parked, the mode stays, and the per-frame pass sends it
    /// on the first frame the body is ready. Then the mode changes.
    /// </summary>
    [Fact]
    public void ARequestWhileTheBodyIsBusyIsParkedAndSentWhenTheBodyIsReady()
    {
        var combat = new CombatState();
        var operations = new Operations();
        var readiness = new Readiness { Ready = false };
        var state = new RuntimeCombatModeState(combat, operations);
        state.BindReadiness(readiness);

        RuntimeCombatModeRequestResult result = state.Request(CombatMode.Magic);

        Assert.Equal(RuntimeCombatModeRequestStatus.Deferred, result.Status);
        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
        Assert.Equal(CombatMode.Magic, state.PendingMode);
        Assert.DoesNotContain("send:Magic", operations.Trace);

        state.ApplyPendingMode();
        Assert.DoesNotContain("send:Magic", operations.Trace);

        readiness.Ready = true;
        state.ApplyPendingMode();

        Assert.Contains("send:Magic", operations.Trace);
        Assert.Equal(CombatMode.Magic, combat.CurrentMode);
        Assert.Null(state.PendingMode);
    }

    [Fact]
    public void ARequestWhileTheBodyIsReadyGoesOutAtOnce()
    {
        var combat = new CombatState();
        var operations = new Operations();
        var state = new RuntimeCombatModeState(combat, operations);
        state.BindReadiness(new Readiness { Ready = true });

        RuntimeCombatModeRequestResult result = state.Request(CombatMode.Magic);

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.Magic, combat.CurrentMode);
        Assert.Null(state.PendingMode);
    }

    [Fact]
    public void NoChangeIsAcceptedWhileTeleporting()
    {
        var combat = new CombatState();
        var operations = new Operations();
        var state = new RuntimeCombatModeState(combat, operations);
        state.BindReadiness(new Readiness { Ready = true, Teleporting = true });

        RuntimeCombatModeRequestResult result = state.Request(CombatMode.Magic);

        Assert.Equal(RuntimeCombatModeRequestStatus.Rejected, result.Status);
        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
        Assert.DoesNotContain("send:Magic", operations.Trace);
    }

    /// <summary>
    /// The server may put the character in a mode of its own accord while a
    /// change is parked. The parked change still goes out when the body is
    /// ready, unless the server has already put the character there.
    /// </summary>
    [Fact]
    public void AParkedChangeTheServerHasAlreadyMadeIsDropped()
    {
        var combat = new CombatState();
        var operations = new Operations();
        var readiness = new Readiness { Ready = false };
        var state = new RuntimeCombatModeState(combat, operations);
        state.BindReadiness(readiness);
        state.Request(CombatMode.Magic);

        combat.SetCombatMode(CombatMode.Magic);
        readiness.Ready = true;
        state.ApplyPendingMode();

        Assert.DoesNotContain("send:Magic", operations.Trace);
        Assert.Null(state.PendingMode);
    }

    private sealed class Readiness : IRuntimeCombatModeReadiness
    {
        public bool Ready { get; set; }
        public bool Teleporting { get; set; }
        public bool IsInReadyPosition(CombatMode mode) => Ready;
        public bool IsTeleportInProgress => Teleporting;
    }

    [Fact]
    public void OutsideWorld_IsCompleteNoOp()
    {
        var combat = new CombatState();
        var operations = new Operations { IsInWorld = false };
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Inactive, result.Status);
        Assert.Empty(operations.Trace);
        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
    }

    [Fact]
    public void PeaceWithBow_OrdersIntentSendThenLocalState()
    {
        var combat = new CombatState();
        var operations = new Operations();
        operations.Equipment.Add(new ClientObject
        {
            ObjectId = 1u,
            CurrentlyEquippedLocation = EquipMask.MissileWeapon,
            Type = ItemType.MissileWeapon,
            CombatUse = 2,
        });
        combat.CombatModeChanged += mode =>
            operations.Trace.Add($"state:{mode}");
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.Missile, result.Mode);
        Assert.Equal(
            ["intent", "equipment", "send:Missile", "state:Missile"],
            operations.Trace);
    }

    [Fact]
    public void ActiveCombat_LeavesWithoutEquipmentQuery()
    {
        var combat = new CombatState();
        combat.SetCombatMode(CombatMode.Magic);
        var operations = new Operations();
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.NonCombat, result.Mode);
        Assert.DoesNotContain("equipment", operations.Trace);
    }

    [Fact]
    public void IncompatibleHeldItem_RejectsAfterExplicitIntent()
    {
        var combat = new CombatState();
        var operations = new Operations();
        operations.Equipment.Add(new ClientObject
        {
            ObjectId = 1u,
            Name = "Torch",
            CurrentlyEquippedLocation = EquipMask.Held,
            Type = ItemType.Misc,
        });
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Toggle();

        Assert.Equal(RuntimeCombatModeRequestStatus.Rejected, result.Status);
        Assert.Equal(
            "You can't enter combat mode while wielding the Torch",
            result.Notice);
        Assert.Equal(["intent", "equipment"], operations.Trace);
    }

    [Fact]
    public void TransportFailure_DoesNotPublishLocalMode()
    {
        var combat = new CombatState();
        var operations = new Operations { ThrowOnSend = true };
        var state = new RuntimeCombatModeState(combat, operations);

        Assert.Throws<InvalidOperationException>(() => state.Toggle());

        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
        Assert.Equal(["intent", "equipment", "send:Melee"], operations.Trace);
    }

    [Fact]
    public void ExplicitRequestSendsChosenPluginModeWithoutEquipmentGuessing()
    {
        var combat = new CombatState();
        var operations = new Operations();
        combat.CombatModeChanged += mode =>
            operations.Trace.Add($"state:{mode}");
        var state = new RuntimeCombatModeState(combat, operations);

        RuntimeCombatModeRequestResult result = state.Request(CombatMode.Magic);

        Assert.Equal(RuntimeCombatModeRequestStatus.Sent, result.Status);
        Assert.Equal(CombatMode.Magic, result.Mode);
        Assert.Equal(
            ["intent", "send:Magic", "state:Magic"],
            operations.Trace);
    }

    private sealed class Operations : IRuntimeCombatModeOperations
    {
        public bool IsInWorld { get; init; } = true;
        public bool ThrowOnSend { get; init; }
        public List<ClientObject> Equipment { get; } = [];
        public List<string> Trace { get; } = [];

        public IReadOnlyList<ClientObject> GetOrderedEquipment()
        {
            Trace.Add("equipment");
            return Equipment;
        }

        public void NotifyExplicitCombatModeRequest() => Trace.Add("intent");

        public void SendChangeCombatMode(CombatMode mode)
        {
            Trace.Add($"send:{mode}");
            if (ThrowOnSend)
                throw new InvalidOperationException("transport");
        }
    }
}
