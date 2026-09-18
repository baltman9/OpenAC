using Silk.NET.Vulkan;

namespace AcDream.App.Rendering.Gpu.Vk;

internal sealed unsafe class VulkanGpuTimerPool : IGpuTimerPool, IDisposable
{
    /// <summary>Distinct named ranges measurable per frame. Two queries each.
    /// Per-stage attribution records a range per batch, so the budget has to
    /// cover a busy frame's worth of them rather than a handful.</summary>
    internal const int MaxScopesPerFrame = 192;

    private readonly Silk.NET.Vulkan.Vk _vk;
    private readonly Device _device;
    private readonly double _timestampPeriodNanoseconds;
    private readonly QueryPool[] _pools;
    private readonly List<string>[] _scopeNames;
    private readonly HashSet<string>[] _scopeNameSet;
    private readonly Dictionary<string, double> _resolved = new(StringComparer.Ordinal);
    private readonly List<(string Name, double Milliseconds)> _lastResolved = [];

    private int _currentSlot;
    private bool _disposed;

    internal VulkanGpuTimerPool(
        Silk.NET.Vulkan.Vk vk,
        PhysicalDevice physicalDevice,
        Device device,
        int flightCount,
        bool isSupported)
    {
        _vk = vk ?? throw new ArgumentNullException(nameof(vk));
        _device = device;
        IsSupported = isSupported;

        vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);
        _timestampPeriodNanoseconds = properties.Limits.TimestampPeriod;

        _pools = new QueryPool[flightCount];
        _scopeNames = new List<string>[flightCount];
        _scopeNameSet = new HashSet<string>[flightCount];
        for (int slot = 0; slot < flightCount; slot++)
        {
            _scopeNames[slot] = [];
            _scopeNameSet[slot] = new HashSet<string>(StringComparer.Ordinal);
            if (!isSupported)
                continue;

            var create = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = MaxScopesPerFrame * 2,
            };
            VulkanInterop.Check(
                _vk.CreateQueryPool(_device, &create, null, out QueryPool pool),
                "vkCreateQueryPool (timer pool)");
            _pools[slot] = pool;
        }
    }

    public bool IsSupported { get; }

    internal void BeginSlot(int slotIndex)
    {
        if (!IsSupported || _disposed)
            return;

        _currentSlot = slotIndex;
        // Per frame, not per session: a line that reported every drop since the
        // client started would keep saying so long after the busy frame.
        DroppedScopes = 0;
        List<string> names = _scopeNames[slotIndex];
        if (names.Count > 0)
        {
            Resolve(slotIndex, names);
            names.Clear();
            _scopeNameSet[slotIndex].Clear();
        }

        _vk.ResetQueryPool(_device, _pools[slotIndex], 0, MaxScopesPerFrame * 2);
    }

    internal IDisposable BeginScope(CommandBuffer commands, string scopeName) =>
        BeginScope(commands, scopeName, sequential: false);

    /// <summary>Opens a measured range.
    /// <para><paramref name="sequential"/> false starts the range at the top of
    /// the pipeline: the right reading for a range that brackets the whole
    /// frame, because the timestamp lands as the frame's first command is
    /// reached.</para>
    /// <para><paramref name="sequential"/> true starts it after every earlier
    /// command has completed, so the range reports only its own work. Stage
    /// ranges recorded back to back then add up instead of each one counting
    /// the wait for everything before it.</para></summary>
    internal IDisposable BeginScope(CommandBuffer commands, string scopeName, bool sequential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName);
        if (!IsSupported || _disposed)
            return NullScope.Instance;

        List<string> names = _scopeNames[_currentSlot];
        if (names.Count >= MaxScopesPerFrame)
        {
            // Past the budget the range is not recorded at all. Silence would
            // read as "this stage cost nothing", so say so instead.
            DroppedScopes++;
            return NullScope.Instance;
        }
        if (!_scopeNameSet[_currentSlot].Add(scopeName))
        {
            throw new InvalidOperationException(
                $"GPU timer scope '{scopeName}' has already been measured this frame. Two ranges " +
                "sharing a name would silently report whichever finished last.");
        }

        int index = names.Count;
        names.Add(scopeName);
        _vk.CmdWriteTimestamp2(
            commands,
            sequential ? PipelineStageFlags2.AllCommandsBit : PipelineStageFlags2.TopOfPipeBit,
            _pools[_currentSlot],
            (uint)(index * 2));
        return new ActiveScope(this, commands, _currentSlot, index);
    }

    private void EndScope(CommandBuffer commands, int slotIndex, int index)
    {
        if (!IsSupported || _disposed)
            return;
        _vk.CmdWriteTimestamp2(
            commands,
            PipelineStageFlags2.BottomOfPipeBit,
            _pools[slotIndex],
            (uint)((index * 2) + 1));
    }

    private void Resolve(int slotIndex, List<string> names)
    {
        int queryCount = names.Count * 2;
        Span<ulong> results = stackalloc ulong[MaxScopesPerFrame * 2];
        fixed (ulong* first = results)
        {
            Result status = _vk.GetQueryPoolResults(
                _device,
                _pools[slotIndex],
                0,
                (uint)queryCount,
                (nuint)(queryCount * sizeof(ulong)),
                first,
                sizeof(ulong),
                QueryResultFlags.Result64Bit);
            // NotReady is normal and expected the first time a slot recurs on a
            // fast GPU; the previous value simply stands. It is never worth a
            // wait, which is the whole design.
            if (status != Result.Success)
                return;
        }

        ResolveGeneration++;
        _lastResolved.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            ulong start = results[i * 2];
            ulong end = results[(i * 2) + 1];
            if (end <= start)
                continue;
            double nanoseconds = (end - start) * _timestampPeriodNanoseconds;
            double milliseconds = nanoseconds / 1_000_000d;
            _resolved[names[i]] = milliseconds;
            _lastResolved.Add((names[i], milliseconds));
        }
    }

    /// <summary>Counts completed read-backs. A reader that only wants each
    /// measured frame once compares this against what it saw last.</summary>
    public int ResolveGeneration { get; private set; }

    public int DroppedScopes { get; private set; }

    /// <summary>The ranges of the one frame the last read-back covered — not
    /// the running table, which keeps a stale value for a range that frame did
    /// not record.</summary>
    public IReadOnlyList<(string Name, double Milliseconds)> LastResolved => _lastResolved;

    public bool TryResolve(string scopeName, out double milliseconds) =>
        _resolved.TryGetValue(scopeName, out milliseconds);

    public bool TryTakeResolved(string scopeName, out double milliseconds)
    {
        if (!_resolved.TryGetValue(scopeName, out milliseconds))
            return false;
        _resolved.Remove(scopeName);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (QueryPool pool in _pools)
        {
            if (pool.Handle != 0)
                _vk.DestroyQueryPool(_device, pool, null);
        }
    }

    private sealed class ActiveScope(
        VulkanGpuTimerPool pool,
        CommandBuffer commands,
        int slotIndex,
        int index) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended)
                return;
            _ended = true;
            pool.EndScope(commands, slotIndex, index);
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
