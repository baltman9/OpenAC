using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using DatReaderWriter;
using DatReaderWriter.DBObjs;

namespace AcDream.Runtime.Physics;

/// <summary>
/// Where a host's animation content comes from: the table that names an
/// object's motion cycles and the reader that loads the authored frames those
/// cycles play.
/// </summary>
/// <remarks>
/// A host binds this once. A host that has no content bound, or no table for a
/// particular object, simply gets bodies that play nothing — which is the
/// honest answer and the one a host without content has always given.
/// </remarks>
internal interface IRuntimeMotionContentSource
{
    /// <summary>Loads the authored frames a cycle plays.</summary>
    IAnimationLoader AnimationLoader { get; }

    /// <summary>
    /// The table of motion cycles stored under this id, or null when the id is
    /// zero or the content is not to hand.
    /// </summary>
    MotionTable? TryGetMotionTable(uint motionTableId);

    /// <summary>
    /// The part layout stored under this id, or null when the id is zero or
    /// the content is not to hand. A body's cycles are played against its
    /// parts, so the layout is as much animation content as the table is.
    /// </summary>
    Setup? TryGetSetup(uint setupId);
}

/// <summary>
/// The ordinary animation content source: motion tables read straight out of
/// the content files, and a loader over the same files.
/// </summary>
internal sealed class RuntimeDatMotionContentSource : IRuntimeMotionContentSource
{
    private readonly IDatReaderWriter _dats;

    public RuntimeDatMotionContentSource(
        IDatReaderWriter dats,
        IAnimationLoader animationLoader)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        AnimationLoader = animationLoader
            ?? throw new ArgumentNullException(nameof(animationLoader));
    }

    public IAnimationLoader AnimationLoader { get; }

    public MotionTable? TryGetMotionTable(uint motionTableId) =>
        motionTableId == 0u ? null : _dats.Get<MotionTable>(motionTableId);

    public Setup? TryGetSetup(uint setupId) =>
        setupId == 0u ? null : _dats.Get<Setup>(setupId);
}

/// <summary>
/// Builds a body's motion simulation state out of whatever animation content
/// the host has bound.
/// </summary>
/// <remarks>
/// The three ways a body comes by its cycles are the three entries here: from
/// its own motion table, from the default animation its part layout names, or
/// not at all. Which one applies is the caller's to decide from the creation
/// description; what the state ends up holding is decided once, here, so that
/// two hosts reading the same description build the same body.
/// </remarks>
internal sealed class RuntimeMotionStateBuilder
{
    private readonly IRuntimeMotionContentSource _content;

    public RuntimeMotionStateBuilder(IRuntimeMotionContentSource content) =>
        _content = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>A body that plays nothing, at the scale it wears.</summary>
    public RuntimeRemoteAnimationState CreateWithoutSequencer(float scale) =>
        new() { Scale = scale };

    /// <summary>
    /// A body whose cycles come from the motion table under this id, put to
    /// the stance and command the creation description asked for. The state
    /// carries no sequencer when the host has no table for that id.
    /// </summary>
    public RuntimeRemoteAnimationState CreateFromMotionTable(
        Setup setup,
        uint motionTableId,
        float scale,
        CreateObject.ServerMotionState? wireState)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return new RuntimeRemoteAnimationState
        {
            Scale = scale,
            Sequencer = _content.TryGetMotionTable(motionTableId)
                is { } motionTable
                    ? SpawnMotionInitializer.Create(
                        setup,
                        motionTable,
                        _content.AnimationLoader,
                        wireState)
                    : null,
        };
    }

    /// <summary>
    /// A body with no motion table of its own, which plays only the one
    /// animation its part layout names. The sequencer is built over an empty
    /// table, so nothing but that animation can ever be asked of it.
    /// </summary>
    public RuntimeRemoteAnimationState CreateFromDefaultAnimation(
        Setup setup,
        float scale)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return new RuntimeRemoteAnimationState
        {
            Scale = scale,
            Sequencer = new AnimationSequencer(
                setup,
                new MotionTable(),
                _content.AnimationLoader),
        };
    }

    /// <summary>
    /// A body's motion state built from nothing but the creation description
    /// the server sent: its part layout, the table its cycles come from, the
    /// stance and command it arrived in, and the scale it wears.
    /// </summary>
    /// <remarks>
    /// This is the whole of what a client needs to build a body, so a client
    /// with no richer maker of its own has one here. The order of the three
    /// ways a body comes by its cycles is the order a client with a window
    /// tries them in: its own table first, then the one animation its part
    /// layout names, then nothing at all. A description that names no part
    /// layout, or one this client's content has no layout for, yields a body
    /// that plays nothing and still carries its scale.
    /// </remarks>
    public RuntimeRemoteAnimationState CreateFromSpawn(
        WorldSession.EntitySpawn spawn,
        float scale)
    {
        uint setupId = spawn.Physics?.SetupTableId ?? spawn.SetupTableId ?? 0u;
        if (_content.TryGetSetup(setupId) is not { } setup)
            return CreateWithoutSequencer(scale);

        uint motionTableId = spawn.Physics?.MotionTableId
            ?? spawn.MotionTableId
            ?? (uint)setup.DefaultMotionTable;
        RuntimeRemoteAnimationState fromTable = CreateFromMotionTable(
            setup,
            motionTableId,
            scale,
            spawn.MotionState);
        if (fromTable.Sequencer is not null)
            return fromTable;

        if ((uint)setup.DefaultAnimation == 0u)
            return fromTable;

        RuntimeRemoteAnimationState fromDefault =
            CreateFromDefaultAnimation(setup, scale);
        return fromDefault.Sequencer?.HasCurrentNode == true
            ? fromDefault
            : fromTable;
    }

    /// <summary>
    /// The stance and command a creation description resolves to against this
    /// host's content, or null when the host has no table for that id. Only
    /// diagnostics need this; building a state already applies it.
    /// </summary>
    public SpawnMotionInitializer.Plan? TryResolvePlan(
        uint motionTableId,
        CreateObject.ServerMotionState? wireState) =>
        _content.TryGetMotionTable(motionTableId) is { } motionTable
            ? SpawnMotionInitializer.ResolvePlan(motionTable, wireState)
            : null;

    /// <summary>
    /// Puts an existing sequencer back to the stance and command a fresh
    /// creation description asks for, for a body whose creation was overtaken
    /// while it was being built. False when the host has no table for the id,
    /// in which case the sequencer is left exactly as it was.
    /// </summary>
    public bool TryReinitialize(
        AnimationSequencer sequencer,
        uint motionTableId,
        CreateObject.ServerMotionState? wireState)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        if (_content.TryGetMotionTable(motionTableId) is not { } motionTable)
            return false;
        SpawnMotionInitializer.Reinitialize(sequencer, motionTable, wireState);
        return true;
    }
}
