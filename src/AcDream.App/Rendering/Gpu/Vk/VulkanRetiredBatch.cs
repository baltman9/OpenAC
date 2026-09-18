namespace AcDream.App.Rendering.Gpu.Vk;

/// <summary>
/// Releasing a batch of retired resources, once.
/// <para>A batch is reached through the closure the flight ledger holds, and
/// teardown drains that ledger more than once, so the same list can be handed
/// to the release path twice. The second pass must do nothing: the handles in
/// it are already destroyed and their memory already back in its pool, so
/// releasing them again would destroy dead handles and return ranges to a
/// block that no longer exists. Emptying the list as part of releasing it is
/// what makes the second pass harmless.</para>
/// </summary>
internal static class VulkanRetiredBatch
{
    /// <summary>Releases every entry and empties the batch. A batch released
    /// twice releases once.</summary>
    internal static void DrainOnce<T>(List<T> batch, Action<T> release)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(release);

        foreach (T entry in batch)
            release(entry);

        // Only once everything is out: a release that throws leaves the rest
        // in the batch rather than dropping them on the floor.
        batch.Clear();
    }
}
