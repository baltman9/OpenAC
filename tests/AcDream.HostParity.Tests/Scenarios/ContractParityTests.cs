using AcDream.Core.Net.Messages;
using AcDream.Core.Quests;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The character's contracts, as a plugin reads them. Both clients answer
/// this, so the difference was quiet: one handed a plugin contracts with
/// their authored names and descriptions and the other handed over the same
/// contracts by number with empty strings where the words should be, because
/// each client decided for itself where the table of words came from.
///
/// It is the runtime's table now, filled by the pass that reads the
/// installed files for both clients. Without those files a contract still
/// reports -- id, stage, progress -- with no words, and the guide says so.
///
/// Mutation check (2026-09-20), run: having the runtime project contracts
/// with no table however one was bound turned
/// <see cref="WithTheWordsBothClientsReadTheSameNames"/> red and left the
/// rest green.
/// </summary>
public sealed class ContractParityTests
{
    private const uint Hunt = 0x0000_0010u;

    [Fact]
    public void WithoutTheWordsBothClientsStillReportTheContract() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Track(arm);

            transcript.Step("a plugin reads the contracts");
            Record(transcript, arm.Host.State.Contracts);

            ContractSnapshot only = Assert.Single(arm.Host.State.Contracts);
            Assert.Equal(Hunt, only.ContractId);
            Assert.Equal(string.Empty, only.Name);
        });

    [Fact]
    public void WithTheWordsBothClientsReadTheSameNames() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Track(arm);
            // What the shared content pass hands the runtime when this
            // session holds a lease on the installed files.
            arm.Runtime.ContractsOwner.BindCatalog(static () => new ContractCatalog(
                new Dictionary<uint, ContractEntry>
                {
                    [Hunt] = ContractEntry.Unknown with
                    {
                        ContractId = Hunt,
                        ContractName = "Tusker Hunt",
                        Description = "Thin the herd.",
                    },
                }));

            transcript.Step("a plugin reads the contracts");
            Record(transcript, arm.Host.State.Contracts);

            ContractSnapshot only = Assert.Single(arm.Host.State.Contracts);
            // Said outright: two clients that both read nothing would write
            // identical transcripts.
            Assert.Equal("Tusker Hunt", only.Name);
            Assert.Equal("Thin the herd.", only.Description);
        });

    private static void Track(ParityArm arm) =>
        arm.Runtime.ContractsOwner.ApplyUpdate(new ContractTrackerUpdate(
            new ContractTracker(
                1u,
                Hunt,
                ContractStage.ProgressCounter,
                0d,
                0d,
                new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)),
            Delete: false,
            SetAsDisplay: true));

    private static void Record(
        ParityTranscript transcript,
        IReadOnlyList<ContractSnapshot> contracts)
    {
        transcript.Record("contracts.count", contracts.Count);
        for (int index = 0; index < contracts.Count; index++)
        {
            transcript.Record($"contracts[{index}].id", contracts[index].ContractId);
            transcript.Record($"contracts[{index}].stage", contracts[index].Stage);
            transcript.Record($"contracts[{index}].name", contracts[index].Name);
            transcript.Record(
                $"contracts[{index}].description", contracts[index].Description);
            transcript.Record($"contracts[{index}].status", contracts[index].Status);
            transcript.Record(
                $"contracts[{index}].displayed", contracts[index].IsDisplayed);
        }
    }
}
