using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin asking whether a shot would reach a creature. The flight is
/// tested against the session's own collision world, so a client with no
/// window answers exactly as one with a window does -- and both really
/// answer, rather than agreeing that neither can tell.
/// </summary>
public sealed class ProjectilePathParityTests
{
    private const uint Buried = 0x50000060u;

    [Fact]
    public void AskingWhetherAShotGetsThereIsAnsweredTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.Add(
                arm.Runtime,
                Buried,
                ParityWorld.PlayerX + 12f,
                ParityWorld.MonsterObject(Buried),
                z: ParityPlayerBody.GroundHeight - 6f);
            IProjectileAutomation projectiles = arm.Host.Automation.Projectiles;

            transcript.Step("can it be asked");
            transcript.Record("available", projectiles.IsAvailable);
            Assert.True(projectiles.IsAvailable);

            foreach (PluginProjectilePathKind kind in
                Enum.GetValues<PluginProjectilePathKind>())
            {
                transcript.Step($"nearest creature, {kind}");
                PluginProjectilePathResult near = Ask(
                    projectiles, ParityWorld.Monster, kind);
                Record(transcript, near);
                Assert.NotEqual(PluginProjectilePathStatus.Unavailable, near.Status);

                transcript.Step($"creature behind others, {kind}");
                PluginProjectilePathResult far = Ask(
                    projectiles, ParityWorld.SecondMonster, kind);
                Record(transcript, far);
                Assert.NotEqual(PluginProjectilePathStatus.Unavailable, far.Status);
            }

            // The ground is the one solid thing in this world, so a creature
            // under it is one no shot can reach.
            transcript.Step("creature with the ground in the way");
            PluginProjectilePathResult buried = Ask(
                projectiles, Buried, PluginProjectilePathKind.Straight);
            Record(transcript, buried);
            Assert.Equal(PluginProjectilePathStatus.Blocked, buried.Status);
            Assert.Equal(
                PluginProjectilePathStatus.Clear,
                Ask(projectiles, ParityWorld.Monster, PluginProjectilePathKind.Straight)
                    .Status);

            transcript.Step("nothing by that id");
            Record(transcript, Ask(
                projectiles, 0x5000FFFFu, PluginProjectilePathKind.Straight));
        });

    private static PluginProjectilePathResult Ask(
        IProjectileAutomation projectiles,
        uint target,
        PluginProjectilePathKind kind) =>
        projectiles.EvaluatePath(
            target,
            kind,
            PluginAttackHeight.Medium,
            projectileRadius: 0.4f,
            stepDistance: 0.7f,
            maximumCollisionChecks: 500);

    private static void Record(
        ParityTranscript transcript,
        in PluginProjectilePathResult result)
    {
        transcript.Record("status", result.Status);
        transcript.Record("checks", result.CollisionChecks);
        transcript.Record("stopped by", $"0x{result.BlockingObjectId:X8}");
    }
}
