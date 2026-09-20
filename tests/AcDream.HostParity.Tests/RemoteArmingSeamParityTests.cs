using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The seam the shared arming asks its host about: which body this client is
/// holding, the centre it measures a place from, and where a thing it may be
/// ordered to walk at is.
///
/// Both clients have to answer every one of them. A client that leaves one
/// null gets the shared arming's own fallback, which would quietly be a
/// different answer for the same body -- exactly the class of difference this
/// census exists to catch. A client that answers with a delegate that does
/// nothing is read as not answering at all.
///
/// Mutation checks (2026-09-20):
/// * leaving <c>InteractionTargetPosition</c> unfilled on the windowless
///   facts turned <see cref="BothClientsAnswerEveryArmingFact"/> red with
///   "InteractionTargetPosition is missing on windowless";
/// * leaving <c>WireOriginToWorld</c> unfilled on the windowed facts turned it
///   red with "WireOriginToWorld is missing on windowed";
/// * restoring each turned it green again.
/// </summary>
public sealed class RemoteArmingSeamParityTests
{
    [Fact]
    public void BothClientsAnswerEveryArmingFact()
    {
        var missing = new List<string>();
        foreach (string host in ParityHost.Both)
        {
            RuntimeRemoteArmingHostFacts facts = FactsFor(host);
            foreach (PropertyInfo member in Members())
            {
                object? answer = member.GetValue(facts);
                if (answer is null || InertBindings.DoesNothing(answer))
                    missing.Add($"{member.Name} is missing on {host}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "A client leaves the shared arming to guess something only the "
            + "client can know, so the same body would be armed differently "
            + "on the two: " + string.Join("; ", missing));
    }

    /// <summary>
    /// The census reads the seam off the type, so a fact added later is
    /// covered without anyone remembering to list it here.
    /// </summary>
    [Fact]
    public void TheSeamIsSmallEnoughToBeReadWhole()
    {
        string[] names = Members()
            .Select(static member => member.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["DrawnBody", "InteractionTargetPosition", "WireOriginToWorld"],
            names);
    }

    private static PropertyInfo[] Members() =>
        typeof(RuntimeRemoteArmingHostFacts)
            .GetProperties(BindingFlags.Public
                | BindingFlags.Instance)
            .Where(static member => member.Name != "EqualityContract")
            .OrderBy(static member => member.Name, StringComparer.Ordinal)
            .ToArray();

    private static RuntimeRemoteArmingHostFacts FactsFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedFacts()
            : RuntimeRemoteArmingHostFacts.FromRecords(
                new RuntimeEntityObjectLifetime());

    /// <summary>
    /// The windowed client's own facts, read off its real controller. Nothing
    /// is called, so the controller needs none of its own parts: what is
    /// asserted is which delegates it hands over and whether they have bodies.
    /// </summary>
    private static RuntimeRemoteArmingHostFacts WindowedFacts() =>
        ((LiveEntityMotionRuntimeController)RuntimeHelpers
            .GetUninitializedObject(
                typeof(LiveEntityMotionRuntimeController)))
        .HostFacts;
}
