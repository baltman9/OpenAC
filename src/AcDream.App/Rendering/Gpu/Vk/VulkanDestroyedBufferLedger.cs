using System.Runtime.InteropServices;

namespace AcDream.App.Rendering.Gpu.Vk;

/// <summary>
/// Buffer handles destroyed since each flight slot's previous frame start,
/// with a cursor per slot. A slot's frame takes the handles it has not seen
/// so its descriptor sets can drop them; the prefix every slot has already
/// consumed is dropped on each take, so the list holds at most the frames
/// in flight worth of destructions. Destruction normally runs on the render
/// thread inside BeginFrame, but a release can run inline on the disposing
/// thread once the flight ledger is torn down, so both sides lock.
/// </summary>
internal sealed class VulkanDestroyedBufferLedger
{
    private readonly List<ulong> _handles = [];
    private readonly int[] _seen;
    private readonly object _sync = new();

    public VulkanDestroyedBufferLedger(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        _seen = new int[slotCount];
    }

    public int SlotCount => _seen.Length;

    /// <summary>Handles still held for some slot that has not consumed them.</summary>
    public int PendingCount
    {
        get { lock (_sync) return _handles.Count; }
    }

    public void Note(ulong handle)
    {
        lock (_sync)
            _handles.Add(handle);
    }

    /// <summary>
    /// The handles destroyed since this slot's previous take. The span is over
    /// the list's backing array and is consumed before the next take; a
    /// concurrent <see cref="Note"/> appends past its end or grows into a new
    /// array, neither of which changes what the span reads.
    /// </summary>
    public ReadOnlySpan<ulong> Take(int slot)
    {
        lock (_sync)
        {
            int seen = _seen[slot];
            // The prefix every slot has consumed, this slot's own previous
            // cursor included: what this take returns starts at that cursor.
            int consumedByAll = seen;
            foreach (int cursor in _seen)
                consumedByAll = Math.Min(consumedByAll, cursor);
            _seen[slot] = _handles.Count;
            if (consumedByAll > 0)
            {
                _handles.RemoveRange(0, consumedByAll);
                for (int i = 0; i < _seen.Length; i++)
                    _seen[i] -= consumedByAll;
                seen -= consumedByAll;
            }
            return CollectionsMarshal.AsSpan(_handles)[seen..];
        }
    }
}
