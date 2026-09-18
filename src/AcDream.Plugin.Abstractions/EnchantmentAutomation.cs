namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginTrackedEnchantment(
    uint TargetObjectId,
    uint SpellId,
    uint Family,
    int Quality,
    bool IsUntargeted,
    double SecondsRemaining);

public interface IEnchantmentAutomation
{
    /// <summary>Forget locally inferred timers; zero clears every reported target.</summary>
    void ForgetReported(uint targetObjectId = 0u) { }

    IReadOnlyList<PluginTrackedEnchantment> Capture(uint targetObjectId) =>
        Array.Empty<PluginTrackedEnchantment>();

    bool ReportCast(
        uint targetObjectId,
        uint spellId,
        double durationSeconds) => false;
}
