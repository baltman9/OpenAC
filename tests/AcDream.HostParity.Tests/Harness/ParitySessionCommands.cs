using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Opening and closing the world connection, the way each client's own
/// session commands reach it. Both arms are given the same one, over their
/// own session, so what a scenario compares is each client's command adapter
/// rather than two different ways of ending a session.
/// </summary>
internal sealed class ParitySessionCommands(LiveSessionHost session)
    : IRuntimeSessionCommands
{
    private readonly LiveSessionHost _session = session
        ?? throw new ArgumentNullException(nameof(session));

    public RuntimeSessionStartResult Start(
        RuntimeGenerationToken expectedGeneration) =>
        _session.Start(expectedGeneration);

    public RuntimeSessionStartResult Reconnect(
        RuntimeGenerationToken expectedGeneration) =>
        _session.Reconnect(expectedGeneration);

    public RuntimeTeardownAcknowledgement Stop(
        RuntimeGenerationToken expectedGeneration) =>
        _session.Stop(expectedGeneration);
}
