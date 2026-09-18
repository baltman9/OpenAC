using AcDream.App.Rendering.Vfx;
using AcDream.Core.Physics;
using AcDream.Core.World;

namespace AcDream.App.Rendering;

internal interface IAnimationHookCaptureSink
{
    void Capture(uint ownerLocalId, AnimationSequencer sequencer);

    /// <summary>
    /// <see cref="Capture"/> as a callback, bound once by the sink. The
    /// physics tick takes a callback and runs for every live entity on every
    /// frame; handing it a method group there would build a delegate per
    /// entity per frame.
    /// </summary>
    Action<uint, AnimationSequencer> CaptureCallback { get; }
}

internal sealed class AnimationHookCaptureSink : IAnimationHookCaptureSink
{
    private readonly AnimationHookFrameQueue _queue;

    public AnimationHookCaptureSink(AnimationHookFrameQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        CaptureCallback = Capture;
    }

    public Action<uint, AnimationSequencer> CaptureCallback { get; }

    public void Capture(uint ownerLocalId, AnimationSequencer sequencer) =>
        _queue.Capture(ownerLocalId, sequencer);
}

internal interface IEntityRootPosePublisher
{
    void UpdateRoot(WorldEntity entity);
}

internal sealed class EntityRootPosePublisher : IEntityRootPosePublisher
{
    private readonly EntityEffectPoseRegistry _poses;

    public EntityRootPosePublisher(EntityEffectPoseRegistry poses) =>
        _poses = poses ?? throw new ArgumentNullException(nameof(poses));

    public void UpdateRoot(WorldEntity entity) => _poses.UpdateRoot(entity);
}
