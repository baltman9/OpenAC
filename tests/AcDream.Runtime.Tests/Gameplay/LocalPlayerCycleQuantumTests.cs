using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// What one step of a character driving its own animation cycles is: where
/// its travel and its turn come from, how often the cycle is asked for them,
/// in what order the hooks it reached are taken, and what the step leaves.
/// </summary>
/// <remarks>
/// This is the step a client with a window has always taken. It is written
/// down here so that giving every client the same step cannot quietly change
/// it.
/// </remarks>
public class LocalPlayerCycleQuantumTests
{
    /// <summary>A run cycle: a tenth of a metre forward every step.</summary>
    private const float TravelPerStep = 0.1f;

    /// <summary>A turn cycle: a fortieth of a turn to the left per step.</summary>
    private const float TurnPerStep = MathF.Tau / 40f;

    /// <summary>Long enough that every frame is worth exactly one step.</summary>
    private static float OneStep => PhysicsBody.MinQuantum + 0.001f;

    private static readonly Vector3 Start = new(96f, 96f, 50f);

    private static PhysicsEngine FlatGround()
    {
        var engine = new PhysicsEngine();
        var heights = new byte[81];
        Array.Fill(heights, (byte)50);
        var table = new float[256];
        for (int i = 0; i < 256; i++)
            table[i] = i * 1f;
        engine.AddLandblock(
            0xA9B4FFFFu,
            new TerrainSurface(heights, table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
        return engine;
    }

    /// <summary>
    /// A character standing still on flat ground, given long enough to have
    /// settled onto it. A body still falling loses its footing on the first
    /// step, and a character off its feet does not travel.
    /// </summary>
    private static PlayerMovementController Standing()
    {
        var controller = new PlayerMovementController(FlatGround());
        controller.SeedPlacementForTest(Start, 0x0001, Start);
        controller.Yaw = 0f;
        for (int settling = 0; settling < 5; settling++)
            controller.Update(OneStep, new MovementInput());
        return controller;
    }

    /// <summary>
    /// A hand-built cycle: it carries the character forward and turns it to
    /// the left by the same amount every step, and writes down what it was
    /// asked for.
    /// </summary>
    private sealed class HandBuiltCycle
    {
        internal readonly List<string> Log = new();
        internal float Travel;
        internal float Turn;

        internal void Advance(float deltaSeconds, MotionDeltaFrame output)
        {
            Log.Add(FormattableString.Invariant($"advance {deltaSeconds:F6}"));
            output.Reset();
            output.Origin = new Vector3(Travel, 0f, 0f);
            output.Orientation =
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Turn);
        }

        internal void CaptureHooks() => Log.Add("hooks");
    }

    /// <summary>
    /// Somewhere for the motions the character is told to play to go. A
    /// character that has one is carried by its cycles; one that has none is
    /// shoved along by its declared speed instead, which is a different
    /// character and not the one described here.
    /// </summary>
    private sealed class PlaysEverything : IInterpretedMotionSink
    {
        public bool ApplyMotion(uint motion, float speed) => true;

        public bool StopMotion(uint motion) => true;
    }

    private static HandBuiltCycle Attach(
        PlayerMovementController controller,
        float travel = TravelPerStep,
        float turn = TurnPerStep)
    {
        var cycle = new HandBuiltCycle { Travel = travel, Turn = turn };
        controller.AttachAnimationRootMotionSource(
            cycle.Advance,
            cycle.CaptureHooks);
        controller.Motion.DefaultSink = new PlaysEverything();
        return cycle;
    }

    /// <summary>
    /// The first step a standing character is carried by takes it off the
    /// ground plane it was resting on and it spends a step or two finding its
    /// feet again, which is the body's business and not the cycle's. These
    /// let it do that before anything is measured.
    /// </summary>
    private static void FindItsFeet(PlayerMovementController controller)
    {
        for (int step = 0; step < 3; step++)
            controller.Update(OneStep, new MovementInput());
    }

    private static Vector2 Flat(Vector3 position) =>
        new(position.X, position.Y);

    [Fact]
    public void TheCharacterTravelsExactlyAsFarAsItsCycleCarriesIt()
    {
        PlayerMovementController controller = Standing();
        _ = Attach(controller, turn: 0f);
        FindItsFeet(controller);

        Vector2 before = Flat(controller.Position);
        for (int step = 0; step < 10; step++)
            controller.Update(OneStep, new MovementInput());

        Assert.Equal(
            10f * TravelPerStep,
            Vector2.Distance(Flat(controller.Position), before),
            precision: 4);
        // On flat ground the character is carried along it, not into or off
        // it: it ends back on its feet at the height it set out from.
        Assert.True(controller.CanSendPositionEvent);
        Assert.Equal(Start.Z, controller.Position.Z, precision: 3);
    }

    [Fact]
    public void TheCharacterTurnsExactlyAsFarAsItsCycleTurnsIt()
    {
        PlayerMovementController controller = Standing();
        _ = Attach(controller, travel: 0f);

        for (int step = 0; step < 10; step++)
            controller.Update(OneStep, new MovementInput());

        // A turn costs the character no footing, so every step counts.
        Assert.Equal(
            10f * TurnPerStep,
            NormalizeTurn(controller.Yaw),
            precision: 4);
    }

    [Fact]
    public void ACycleThatCarriesNothingLeavesTheCharacterWhereItStood()
    {
        PlayerMovementController controller = Standing();
        _ = Attach(controller, travel: 0f, turn: 0f);

        // Told to run the whole time. A character carried by its cycles goes
        // nowhere when the cycle goes nowhere; one shoved along by its
        // declared speed would be metres away by now.
        for (int step = 0; step < 20; step++)
            controller.Update(OneStep, new MovementInput(Forward: true));

        Assert.Equal(
            0f,
            Vector2.Distance(Flat(controller.Position), Flat(Start)),
            precision: 5);
        Assert.Equal(0f, NormalizeTurn(controller.Yaw), precision: 5);
    }

    [Fact]
    public void TheCycleIsAskedOncePerStepAndItsHooksTakenAfterTheBodyMoves()
    {
        PlayerMovementController controller = Standing();
        HandBuiltCycle cycle = Attach(controller);

        controller.Update(OneStep, new MovementInput());
        controller.Update(OneStep, new MovementInput());

        Assert.Equal(
            new[]
            {
                FormattableString.Invariant($"advance {OneStep:F6}"),
                "hooks",
                FormattableString.Invariant($"advance {OneStep:F6}"),
                "hooks",
            },
            cycle.Log);
    }

    [Fact]
    public void TravelIsMeasuredAtTheSizeTheCharacterWears()
    {
        PlayerMovementController half = Standing();
        half.ObjectScale = 0.5f;
        _ = Attach(half, turn: 0f);
        FindItsFeet(half);
        Vector2 halfBefore = Flat(half.Position);

        PlayerMovementController whole = Standing();
        _ = Attach(whole, turn: 0f);
        FindItsFeet(whole);
        Vector2 wholeBefore = Flat(whole.Position);

        for (int step = 0; step < 10; step++)
        {
            half.Update(OneStep, new MovementInput());
            whole.Update(OneStep, new MovementInput());
        }

        Assert.Equal(
            Vector2.Distance(Flat(whole.Position), wholeBefore) * 0.5f,
            Vector2.Distance(Flat(half.Position), halfBefore),
            precision: 4);
    }

    [Fact]
    public void ACharacterOffItsFeetTurnsButDoesNotTravel()
    {
        PlayerMovementController controller = Standing();
        _ = Attach(controller);

        // The first step a standing character is carried by takes it off the
        // ground plane it was resting on, so by the end of it the character
        // is off its feet.
        controller.Update(OneStep, new MovementInput());
        Assert.False(controller.CanSendPositionEvent);
        Vector2 before = Flat(controller.Position);
        float turnedBefore = controller.Yaw;

        controller.Update(OneStep, new MovementInput());

        Assert.Equal(
            0f,
            Vector2.Distance(Flat(controller.Position), before),
            precision: 5);
        Assert.Equal(
            TurnPerStep,
            NormalizeTurn(controller.Yaw - turnedBefore),
            precision: 4);
    }

    private static float NormalizeTurn(float radians)
    {
        float turn = radians % MathF.Tau;
        if (turn < 0f)
            turn += MathF.Tau;
        return turn;
    }
}
