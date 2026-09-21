using AcDream.Core.Net.Messages;
using AcDream.Core.Quests;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// What a plugin reads off the character's contracts, and where the authored
/// names and descriptions on them come from. Both clients ask the runtime for
/// this, so the table the words come out of is the runtime's to hold: when it
/// was each client's own, one of them handed plugins contracts with numbers
/// and no words while the other handed over the same contracts named.
///
/// The table is read the first time something asks and not again, so a
/// session that never opens the journal never pays to read it.
/// </summary>
public sealed class RuntimeContractCatalogTests
{
    private const uint Hunt = 0x10u;

    [Fact]
    public void WithNoCatalogTheContractIsReportedByNumberAlone()
    {
        using var state = new RuntimeContractState();
        Track(state);

        ContractSnapshot snapshot = Assert.Single(state.ProjectForPlugins());

        Assert.Equal(Hunt, snapshot.ContractId);
        Assert.Equal(string.Empty, snapshot.Name);
        Assert.Equal(string.Empty, snapshot.Description);
    }

    [Fact]
    public void WithACatalogTheContractIsReportedByName()
    {
        using var state = new RuntimeContractState();
        Track(state);
        state.BindCatalog(() => Catalog());

        ContractSnapshot snapshot = Assert.Single(state.ProjectForPlugins());

        Assert.Equal("Tusker Hunt", snapshot.Name);
        Assert.Equal("Do the thing.", snapshot.Description);
    }

    [Fact]
    public void TheCatalogIsReadOnceHoweverOftenAPluginAsks()
    {
        using var state = new RuntimeContractState();
        Track(state);
        int reads = 0;
        state.BindCatalog(() =>
        {
            reads++;
            return Catalog();
        });

        _ = state.ProjectForPlugins();
        _ = state.ProjectForPlugins();
        _ = state.ProjectForPlugins();

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// A session that never asks never reads the table: the cost belongs to
    /// the plugin that wanted the words, not to every bot that starts up.
    /// </summary>
    [Fact]
    public void ASessionThatNeverAsksNeverReadsTheTable()
    {
        using var state = new RuntimeContractState();
        int reads = 0;
        state.BindCatalog(() =>
        {
            reads++;
            return Catalog();
        });

        Assert.Equal(0, reads);
    }

    /// <summary>
    /// A second source would mean two answers to the same question.
    /// </summary>
    [Fact]
    public void ASecondCatalogIsRefused()
    {
        using var state = new RuntimeContractState();
        state.BindCatalog(Catalog);

        Assert.Throws<InvalidOperationException>(
            () => state.BindCatalog(Catalog));
    }

    private static void Track(RuntimeContractState state) =>
        state.ApplyUpdate(new ContractTrackerUpdate(
            new ContractTracker(
                1u,
                Hunt,
                ContractStage.ProgressCounter,
                0d,
                0d,
                DateTime.UtcNow),
            Delete: false,
            SetAsDisplay: false));

    private static ContractCatalog Catalog() =>
        new(new Dictionary<uint, ContractEntry>
        {
            [Hunt] = ContractEntry.Unknown with
            {
                ContractId = Hunt,
                ContractName = "Tusker Hunt",
                Description = "Do the thing.",
            },
        });
}
