using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using AcDream.Core.Physics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using Xunit;

using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Tests.Physics;

/// <summary>
/// The sequencer can be advanced in two ways: the full call that also builds
/// the blended part poses, and the root-motion-only call for a host that never
/// draws anything. These tests pin that the second is the FIRST with the pose
/// work removed and nothing else changed: the same root frame down to the bit,
/// the same hooks in the same order, the same sequencer state afterwards.
/// </summary>
public sealed class AnimationSequencerRootMotionOnlyTests
{
    // Style and motion ids as the authored tables spell them.
    private const uint NonCombat = 0x8000003Du;
    private const uint Ready = 0x41000003u;
    private const uint Walk = 0x45000005u;
    private const uint Run = 0x44000007u;

    // Animation resource ids in the synthetic fixture.
    private const uint ReadyAnim = 0x200u;        // 4 frames
    private const uint WalkAnim = 0x201u;         // 6 frames
    private const uint RunAnim = 0x202u;          // 6 frames
    private const uint ReadyToWalkLink = 0x203u;  // 2 frames
    private const uint WalkToRunLink = 0x204u;    // 3 frames

    /// <summary>
    /// Authored travel per animation frame. At 30 frames a second this is
    /// 3.0 metres a second forward, the figure the scenarios below are read in.
    /// </summary>
    private const float MetresPerFrame = 0.1f;

    private const float FramesPerSecond = 30f;

    private sealed class Fixture : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _anims = new();
        private readonly Dictionary<Animation, uint> _ids = new();

        public Setup Setup { get; init; } = null!;
        public MotionTable MotionTable { get; init; } = null!;

        public void Register(uint id, Animation anim)
        {
            _anims[id] = anim;
            _ids[anim] = id;
        }

        public Animation? LoadAnimation(uint id) =>
            _anims.TryGetValue(id, out var a) ? a : null;

        public uint IdOf(Animation? anim) =>
            anim is not null && _ids.TryGetValue(anim, out uint id) ? id : 0u;

        public AnimationSequencer NewSequencer() =>
            new AnimationSequencer(Setup, MotionTable, this);
    }

    /// <summary>
    /// An animation that travels <see cref="MetresPerFrame"/> forward and turns
    /// a little on every frame, and fires a hook on odd frames so the hook
    /// order is observable.
    /// </summary>
    private static Animation MakeAnim(int numFrames, int numParts = 2)
    {
        var anim = new Animation();
        for (int f = 0; f < numFrames; f++)
        {
            var partFrame = new AnimationFrame((uint)numParts);
            for (int p = 0; p < numParts; p++)
            {
                // Distinct part poses, so a pose-building difference would show
                // up as a difference in the blended result too.
                partFrame.Frames.Add(new Frame
                {
                    Origin = new Vector3(0.01f * p, 0.02f * f, 0.03f),
                    Orientation = Quaternion.CreateFromAxisAngle(
                        Vector3.UnitZ, 0.07f * (f + p)),
                });
            }

            if ((f & 1) == 1)
                partFrame.Hooks.Add(new SoundHook());

            anim.PartFrames.Add(partFrame);

            anim.PosFrames.Add(new Frame
            {
                Origin = new Vector3(0f, MetresPerFrame, 0f),
                Orientation = Quaternion.CreateFromAxisAngle(
                    Vector3.UnitZ, 0.035f),
            });
        }

        return anim;
    }

    private static MotionData MakeMotionData(
        uint animId, float framerate = FramesPerSecond,
        Vector3? velocity = null, Vector3? omega = null)
    {
        var md = new MotionData();
        QualifiedDataId<Animation> qid = animId;
        md.Anims.Add(new AnimData
        {
            AnimId = qid,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = framerate,
        });
        if (velocity is { } v)
        {
            md.Velocity = v;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasVelocity;
        }
        if (omega is { } o)
        {
            md.Omega = o;
            md.Flags |= DatReaderWriter.Enums.MotionDataFlags.HasOmega;
        }
        return md;
    }

    private static void AddLink(
        MotionTable mt, uint style, uint from, uint to, MotionData md)
    {
        int outer = (int)((style << 16) | (from & 0xFFFFFFu));
        if (!mt.Links.TryGetValue(outer, out var cmd))
        {
            cmd = new MotionCommandData();
            mt.Links[outer] = cmd;
        }
        cmd.MotionData[(int)to] = md;
    }

    private static Fixture BuildFixture()
    {
        var setup = new Setup();
        for (int i = 0; i < 2; i++)
        {
            setup.Parts.Add(0x01000000u + (uint)i);
            setup.DefaultScale.Add(Vector3.One);
        }

        var mt = new MotionTable { DefaultStyle = (DRWMotionCommand)NonCombat };
        mt.StyleDefaults[(DRWMotionCommand)NonCombat] = (DRWMotionCommand)Ready;

        static int CycleKey(uint style, uint substate)
            => (int)((style << 16) | (substate & 0xFFFFFFu));

        mt.Cycles[CycleKey(NonCombat, Ready)] = MakeMotionData(ReadyAnim);
        mt.Cycles[CycleKey(NonCombat, Walk)] = MakeMotionData(
            WalkAnim, velocity: new Vector3(0f, 3.12f, 0f), omega: new Vector3(0f, 0f, 0.4f));
        mt.Cycles[CycleKey(NonCombat, Run)] = MakeMotionData(
            RunAnim, velocity: new Vector3(0f, 4.0f, 0f));

        AddLink(mt, NonCombat, Ready, Walk, MakeMotionData(ReadyToWalkLink));
        AddLink(mt, NonCombat, Walk, Run, MakeMotionData(WalkToRunLink));

        var fixture = new Fixture { Setup = setup, MotionTable = mt };
        fixture.Register(ReadyAnim, MakeAnim(4));
        fixture.Register(WalkAnim, MakeAnim(6));
        fixture.Register(RunAnim, MakeAnim(6));
        fixture.Register(ReadyToWalkLink, MakeAnim(2));
        fixture.Register(WalkToRunLink, MakeAnim(3));
        return fixture;
    }

    // ── exact comparison helpers ─────────────────────────────────────────────

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Bits(float f)
        => BitConverter.SingleToInt32Bits(f).ToString("X8", Inv);

    private static string Bits(double d)
        => BitConverter.DoubleToInt64Bits(d).ToString("X16", Inv);

    private static string Bits(Vector3 v)
        => $"({Bits(v.X)},{Bits(v.Y)},{Bits(v.Z)})";

    private static string Bits(Quaternion q)
        => $"({Bits(q.X)},{Bits(q.Y)},{Bits(q.Z)},{Bits(q.W)})";

    private static string RootFrameBits(Frame frame)
        => $"origin={Bits(frame.Origin)} orientation={Bits(frame.Orientation)}";

    /// <summary>
    /// Every piece of sequencer state a later step can read, bit for bit.
    /// </summary>
    private static string StateBits(AnimationSequencer seq, Fixture fixture)
    {
        var core = seq.Core;
        var sb = new StringBuilder();
        for (var n = core.AnimList.First; n is not null; n = n.Next)
        {
            var node = n.Value;
            sb.Append(Inv, $"{fixture.IdOf(node.Anim):X}");
            sb.Append(Inv, $"@{Bits(node.Framerate)}");
            sb.Append(Inv, $":{node.LowFrame}-{node.HighFrame}");
            if (ReferenceEquals(n, core.FirstCyclicNode)) sb.Append('*');
            if (ReferenceEquals(n, core.CurrAnimNode)) sb.Append('^');
            sb.Append(';');
        }

        sb.Append(Inv, $" frame={Bits(core.FrameNumber)}");
        sb.Append(Inv, $" vel={Bits(core.Velocity)}");
        sb.Append(Inv, $" om={Bits(core.Omega)}");
        sb.Append(Inv, $" style={seq.CurrentStyle:X8}");
        sb.Append(Inv, $" motion={seq.CurrentMotion:X8}");
        sb.Append(Inv, $" mod={Bits(seq.CurrentSpeedMod)}");
        sb.Append(Inv, $" queue={seq.QueueCount}");
        sb.Append(Inv, $" hasNode={seq.HasCurrentNode}");
        return sb.ToString();
    }

    private static Frame NewRootFrame() => new Frame
    {
        Origin = new Vector3(11f, 22f, 33f),
        Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f),
    };

    private sealed class Arm
    {
        public Arm(Fixture fixture, Action<AnimationSequencer> arrange)
        {
            Sequencer = fixture.NewSequencer();
            arrange(Sequencer);
            Sequencer.ConsumePendingHooks();
        }

        public AnimationSequencer Sequencer { get; }
        public Frame Root { get; } = NewRootFrame();
        public List<AnimationHook> Hooks { get; } = new();

        public void StepFull(float dt)
        {
            Sequencer.Advance(dt, Root);
            Hooks.AddRange(Sequencer.ConsumePendingHooks());
        }

        public void StepRootOnly(float dt)
        {
            Sequencer.AdvanceRootMotionOnly(dt, Root);
            Hooks.AddRange(Sequencer.ConsumePendingHooks());
        }
    }

    /// <summary>
    /// Run the same arrangement and the same dt sequence twice — once through
    /// the full call, once through the root-motion-only call — and assert the
    /// two are indistinguishable from the outside.
    /// </summary>
    private static void AssertRootMotionOnlyMatchesFullAdvance(
        Action<AnimationSequencer> arrange, float[] steps)
    {
        var fixture = BuildFixture();
        var full = new Arm(fixture, arrange);
        var rootOnly = new Arm(fixture, arrange);

        Assert.Equal(
            StateBits(full.Sequencer, fixture),
            StateBits(rootOnly.Sequencer, fixture));

        for (int i = 0; i < steps.Length; i++)
        {
            full.StepFull(steps[i]);
            rootOnly.StepRootOnly(steps[i]);

            Assert.Equal(RootFrameBits(full.Root), RootFrameBits(rootOnly.Root));
            Assert.Equal(
                StateBits(full.Sequencer, fixture),
                StateBits(rootOnly.Sequencer, fixture));
            Assert.Equal(full.Hooks.Count, rootOnly.Hooks.Count);
            for (int h = 0; h < full.Hooks.Count; h++)
                Assert.Same(full.Hooks[h], rootOnly.Hooks[h]);
        }

        // The arrangement is only worth running if something actually happened.
        Assert.NotEqual(RootFrameBits(NewRootFrame()), RootFrameBits(full.Root));
    }

    private static float[] Repeat(float dt, int count)
    {
        var steps = new float[count];
        for (int i = 0; i < count; i++)
            steps[i] = dt;
        return steps;
    }

    // ── scenarios ────────────────────────────────────────────────────────────

    private static void ArrangeWalkCycle(AnimationSequencer seq)
    {
        seq.SetCycle(NonCombat, Ready);
        seq.SetCycle(NonCombat, Walk, 1f);
    }

    [Fact]
    public void OneBigStep_CarriesTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(ArrangeWalkCycle, new[] { 1f });

    [Fact]
    public void ManySmallSteps_CarryTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(
            ArrangeWalkCycle, Repeat(1f / 300f, 300));

    [Fact]
    public void StepsShorterThanAFrame_CarryTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(
            ArrangeWalkCycle, Repeat(1f / 120f, 40));

    [Fact]
    public void StepsCrossingFrameBoundariesExactly_CarryTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(
            ArrangeWalkCycle, Repeat(1f / FramesPerSecond, 24));

    [Fact]
    public void StepsStraddlingFrameBoundaries_CarryTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(
            ArrangeWalkCycle, new[] { 0.017f, 0.051f, 0.009f, 0.121f, 0.033f, 0.2f, 0.004f });

    [Fact]
    public void StepsCrossingTheEndOfALinkAnimation_CarryTheSameRootMotion()
        // The link from the idle cycle into the walk cycle is two frames, so a
        // 0.08 s step lands past its end and drains it.
        => AssertRootMotionOnlyMatchesFullAdvance(
            ArrangeWalkCycle, new[] { 0.02f, 0.08f, 0.02f, 0.5f });

    [Fact]
    public void ATransitionBetweenTwoCycles_CarriesTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(
            seq =>
            {
                seq.SetCycle(NonCombat, Ready);
                seq.SetCycle(NonCombat, Walk, 1f);
                seq.SetCycle(NonCombat, Run, 2f);
            },
            Repeat(1f / 60f, 90));

    [Fact]
    public void ALoopingCycleRunFarPastItsEnd_CarriesTheSameRootMotion()
        => AssertRootMotionOnlyMatchesFullAdvance(
            seq => seq.SetCycle(NonCombat, Ready),
            Repeat(1f / 45f, 200));

    [Fact]
    public void ZeroAndNegativeSteps_LeaveBothSequencersAlike()
        => AssertRootMotionOnlyMatchesFullAdvance(
            ArrangeWalkCycle,
            new[] { 0f, -0.01f, 0.05f, 0f, 0.05f, -1f, 0.05f });

    [Fact]
    public void InterleavingTheTwoCallsOnOneSequencer_MatchesTheFullAdvanceThroughout()
    {
        var fixture = BuildFixture();
        var full = new Arm(fixture, ArrangeWalkCycle);
        var mixed = new Arm(fixture, ArrangeWalkCycle);

        float[] steps = Repeat(1f / 72f, 120);
        for (int i = 0; i < steps.Length; i++)
        {
            full.StepFull(steps[i]);
            if ((i % 3) == 0)
                mixed.StepFull(steps[i]);
            else
                mixed.StepRootOnly(steps[i]);

            Assert.Equal(RootFrameBits(full.Root), RootFrameBits(mixed.Root));
            Assert.Equal(
                StateBits(full.Sequencer, fixture),
                StateBits(mixed.Sequencer, fixture));
            Assert.Equal(full.Hooks.Count, mixed.Hooks.Count);
            for (int h = 0; h < full.Hooks.Count; h++)
                Assert.Same(full.Hooks[h], mixed.Hooks[h]);
        }
    }

    [Fact]
    public void AuthoredTravelIsThreeMetresASecondForward()
    {
        // The fixture the scenarios above are read in: 0.1 m of authored travel
        // on each of 30 frames a second, i.e. 3 metres a second forward.
        var fixture = BuildFixture();

        // Same travel with the per-frame turn removed, so a tenth of a second
        // of it is a straight line that can be read in metres.
        var straightAnim = MakeAnim(4);
        foreach (var pos in straightAnim.PosFrames)
            pos.Orientation = Quaternion.Identity;
        fixture.Register(ReadyAnim, straightAnim);

        var seq = fixture.NewSequencer();
        seq.SetCycle(NonCombat, Ready);

        var root = new Frame { Origin = Vector3.Zero, Orientation = Quaternion.Identity };
        for (int i = 0; i < 3; i++)
            seq.AdvanceRootMotionOnly(1f / FramesPerSecond, root);

        Assert.Equal(0.3f, root.Origin.Y, precision: 4);   // 0.1 s at 3 m/s.
        Assert.Equal(0f, root.Origin.X, precision: 5);
        Assert.Equal(0f, root.Origin.Z, precision: 5);
    }

    [Fact]
    public void WithNoAnimationAndNoFrame_NeitherCallTouchesAnything()
    {
        var fixture = BuildFixture();
        var seq = fixture.NewSequencer();
        seq.Reset();

        string before = StateBits(seq, fixture);
        seq.AdvanceRootMotionOnly(0.05f, rootMotionFrame: null);
        Assert.Equal(before, StateBits(seq, fixture));
        Assert.Empty(seq.PendingHooks);
    }
}
