using System.Numerics;
using AcDream.Content;
using AcDream.Core.Physics;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;
using DRWMotionCommand = DatReaderWriter.Enums.MotionCommand;
using DatReaderWriter.Types;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The animation content both clients read the character out of: one part
/// layout, one table of cycles, and three cycles authored by hand -- standing
/// still, running, and turning on the spot.
/// </summary>
/// <remarks>
/// A character carries itself forward by its own cycles, so with no cycles to
/// play neither client moves at all and a scenario proves nothing by agreeing.
/// Both arms read from this one source, built from the same numbers, which is
/// what makes a difference between them a difference in the clients and not in
/// what they were given.
///
/// The numbers are chosen to be easy to check by hand: the run cycle carries
/// the character a tenth of a metre a frame at thirty frames a second, which
/// is three metres a second, and the turn cycle turns it a hundredth of a
/// turn a frame, which is a turn every three and a third seconds.
/// </remarks>
internal sealed class ParityMotionContent : IRuntimeMotionContentSource
{
    /// <summary>The part layout the character's creation description names.</summary>
    internal const uint SetupId = 0x02000001u;

    internal const uint MotionTableId = 0x09000001u;

    /// <summary>Standing still, running, and turning, in that order.</summary>
    internal const uint StandingAnimation = 0x03000001u;

    internal const uint RunningAnimation = 0x03000002u;

    /// <summary>Turning is authored once per direction, as a body turns.</summary>
    internal const uint TurningLeftAnimation = 0x03000003u;

    internal const uint TurningRightAnimation = 0x03000004u;

    /// <summary>The one stance these cycles are authored in.</summary>
    internal const uint NonCombat = 0x8000003Du;

    /// <summary>A tenth of a metre a frame, thirty frames a second.</summary>
    internal const float RunMetresPerFrame = 0.1f;

    /// <summary>A hundredth of a turn a frame, thirty frames a second.</summary>
    internal const float TurnPerFrame = MathF.Tau / 100f;

    /// <summary>
    /// The short cycle the character is carried through when it crosses from
    /// one of the cycles above to another. It is the one cycle here that ends
    /// rather than looping, which is what makes this content say anything
    /// about a cycle finishing: a loop never reaches its end, so a character
    /// playing only loops never reaches a point anybody has to report, and a
    /// movement that waits on such a report would be waiting on nothing.
    /// </summary>
    internal const uint CrossingAnimation = 0x03000005u;

    /// <summary>The cycles the character crosses between.</summary>
    private static readonly uint[] Substates =
    [
        AcDream.Core.Physics.MotionCommand.Ready,
        AcDream.Core.Physics.MotionCommand.RunForward,
        AcDream.Core.Physics.MotionCommand.WalkForward,
        AcDream.Core.Physics.MotionCommand.TurnLeft,
        AcDream.Core.Physics.MotionCommand.TurnRight,
    ];

    private readonly Loader _loader = new();
    private readonly MotionTable _table;

    internal ParityMotionContent()
    {
        _loader.Add(CrossingAnimation, Authored(Vector3.Zero, 0f));
        _loader.Add(StandingAnimation, Authored(Vector3.Zero, 0f));
        _loader.Add(
            RunningAnimation,
            Authored(new Vector3(0f, RunMetresPerFrame, 0f), 0f));
        _loader.Add(
            TurningLeftAnimation, Authored(Vector3.Zero, TurnPerFrame));
        _loader.Add(
            TurningRightAnimation, Authored(Vector3.Zero, -TurnPerFrame));

        _table = new MotionTable
        {
            DefaultStyle = (DRWMotionCommand)NonCombat,
        };
        _table.StyleDefaults[(DRWMotionCommand)NonCombat] =
            (DRWMotionCommand)AcDream.Core.Physics.MotionCommand.Ready;
        Cycle(AcDream.Core.Physics.MotionCommand.Ready, StandingAnimation);
        Cycle(AcDream.Core.Physics.MotionCommand.RunForward, RunningAnimation);
        Cycle(AcDream.Core.Physics.MotionCommand.WalkForward, RunningAnimation);
        Cycle(
            AcDream.Core.Physics.MotionCommand.TurnRight,
            TurningRightAnimation);
        Cycle(
            AcDream.Core.Physics.MotionCommand.TurnLeft,
            TurningLeftAnimation);
        foreach (uint from in Substates)
        {
            var crossings = new MotionCommandData();
            foreach (uint to in Substates)
            {
                if (to != from)
                    crossings.MotionData[(int)to] = Motion(CrossingAnimation);
            }
            _table.Links[(int)((NonCombat << 16) | (from & 0xFFFFFFu))] =
                crossings;
        }
    }

    public IAnimationLoader AnimationLoader => _loader;

    public MotionTable? TryGetMotionTable(uint motionTableId) =>
        motionTableId == MotionTableId ? _table : null;

    public Setup? TryGetSetup(uint setupId)
    {
        if (setupId != SetupId)
            return null;
        var setup = new Setup
        {
            DefaultMotionTable = (QualifiedDataId<MotionTable>)MotionTableId,
        };
        setup.Parts.Add(0x0100AA01u);
        setup.DefaultScale.Add(Vector3.One);
        return setup;
    }

    private void Cycle(uint command, uint animationId) =>
        _table.Cycles[(int)((NonCombat << 16) | (command & 0xFFFFFFu))] =
            Motion(animationId);

    private static MotionData Motion(uint animationId)
    {
        var data = new MotionData();
        data.Anims.Add(new AnimData
        {
            AnimId = (QualifiedDataId<Animation>)animationId,
            LowFrame = 0,
            HighFrame = -1,
            Framerate = 30f,
        });
        return data;
    }

    /// <summary>
    /// Four frames that each carry the character the same way, so the cycle
    /// reads the same wherever in it a step happens to land.
    /// </summary>
    private static Animation Authored(Vector3 travel, float turn)
    {
        var animation = new Animation { Flags = DatReaderWriter.Enums.AnimationFlags.PosFrames };
        for (int frame = 0; frame < 4; frame++)
        {
            var partFrame = new AnimationFrame(1);
            partFrame.Frames.Add(new Frame
            {
                Origin = Vector3.Zero,
                Orientation = Quaternion.Identity,
            });
            animation.PartFrames.Add(partFrame);
            animation.PosFrames.Add(new Frame
            {
                Origin = travel,
                Orientation = turn == 0f
                    ? Quaternion.Identity
                    : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn),
            });
        }
        return animation;
    }

    private sealed class Loader : IAnimationLoader
    {
        private readonly Dictionary<uint, Animation> _animations = new();

        internal void Add(uint id, Animation animation) =>
            _animations[id] = animation;

        public Animation? LoadAnimation(uint requested) =>
            _animations.TryGetValue(requested, out Animation? animation)
                ? animation
                : null;
    }
}
