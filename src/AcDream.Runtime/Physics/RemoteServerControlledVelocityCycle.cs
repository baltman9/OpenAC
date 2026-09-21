namespace AcDream.Runtime.Physics;

internal static class RemoteServerControlledVelocityCycle
{
    private static bool IsPlayerGuid(uint guid) =>
        (guid & 0xFF000000u) == 0x50000000u;
    // Takes the sequence itself rather than a host's animation record: the
    // only thing this ever needed was the cycle the body is playing, and
    // asking for no more than that lets either host call it.
    public static void Apply(
        uint serverGuid,
        AcDream.Core.Physics.AnimationSequencer? sequencer,
        RemoteMotion rm,
        System.Numerics.Vector3 velocity)
    {
        if (rm.Airborne) return;
        if (sequencer is null) return;
        if (rm.MoveTo is { MovementTypeState: not AcDream.Core.Physics.MovementType.Invalid }) return;

        if (IsPlayerGuid(serverGuid))
        {
            return;
        }

        uint currentMotion = sequencer.CurrentMotion;
        if (!AcDream.Core.Physics.ServerControlledLocomotion
            .CanApplyVelocityCycle(currentMotion))
            return;

        var plan = AcDream.Core.Physics.ServerControlledLocomotion
            .PlanFromVelocity(velocity);

        uint style = sequencer.CurrentStyle != 0
            ? sequencer.CurrentStyle
            : 0x8000003Du;

        if (System.Environment.GetEnvironmentVariable("ACDREAM_REMOTE_VEL_DIAG") == "1")
        {
            System.Console.WriteLine(
                $"[UPCYCLE] guid={serverGuid:X8} "
                + $"vel=({velocity.X:F2},{velocity.Y:F2},{velocity.Z:F2}) "
                + $"|v|={velocity.Length():F2} "
                + $"-> motion=0x{plan.Motion:X8} speedMod={plan.SpeedMod:F2} "
                + $"prev=0x{currentMotion:X8} "
                + $"airborne={rm.Airborne} moveTo={rm.MoveTo?.MovementTypeState ?? AcDream.Core.Physics.MovementType.Invalid}");
        }
        sequencer.SetCycle(style, plan.Motion, plan.SpeedMod);
    }

}
