namespace AcDream.Plugin.Abstractions;

/// <summary>Result of one explicit operator recovery operation.</summary>
public readonly record struct PluginRecoveryResult(
    bool Accepted,
    int PreviousCount = 0,
    int CurrentCount = 0,
    string Message = "");

public readonly record struct PluginBusyState(
    int BusyCount, bool PendingInventory, uint AwaitingAppraisal,
    uint UseSource, uint UseTarget, bool AwaitingUseCompletion);

public interface IRecoveryAutomation
{
    PluginBusyState CaptureBusyState() => default;
    PluginRecoveryResult ClearOneBusyReference() => new(
        Accepted: false,
        Message: "Action recovery is unavailable on this host.");
}
