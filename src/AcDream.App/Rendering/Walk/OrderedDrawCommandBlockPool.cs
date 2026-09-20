namespace AcDream.App.Rendering.Walk;

/// <summary>
/// Keeps command-block array sets alive across the producers that own them.
///
/// A block's eleven arrays are sized to the commands the producer holds and
/// reused in place while that producer lives. What they cannot survive today
/// is the producer itself: when a whole set of producers is replaced at once
/// -- every cached entry is dropped when the scene's generation changes --
/// each replacement allocates its arrays again and every departed set becomes
/// garbage, a hundred and sixty megabytes of it on the measured route. The
/// arrays are interchangeable, so a departing producer hands its set here and
/// an arriving one takes the smallest set that fits.
///
/// The pool is bounded by what one generation of producers holds: the
/// commands parked here never exceed the most commands ever live at once, so
/// the arrays retained off a producer are at most a second copy of the live
/// set, and the excess is dropped smallest-first.
/// </summary>
internal sealed class OrderedDrawCommandBlockPool
{
    // Parked sets, ascending by capacity, so a take is the smallest fit.
    private readonly List<OrderedDrawCommandBlock> _free = new();
    private int _parkedCapacity;
    private int _liveCapacity;
    private int _peakLiveCapacity;

    /// <summary>How many takes had to allocate a new array set.</summary>
    internal int AllocationCount { get; private set; }

    /// <summary>How many takes were served from a parked set.</summary>
    internal int ReuseCount { get; private set; }

    /// <summary>Commands the parked sets can hold.</summary>
    internal int ParkedCapacity => _parkedCapacity;

    /// <summary>How many sets are parked.</summary>
    internal int ParkedCount => _free.Count;

    /// <summary>
    /// Hands <paramref name="block"/> back and returns a set that holds at
    /// least <paramref name="capacity"/> commands, empty of contents.
    /// </summary>
    internal OrderedDrawCommandBlock Exchange(
        OrderedDrawCommandBlock block, int capacity)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Park(block);
        return Take(capacity);
    }

    /// <summary>Takes a set that holds at least <paramref name="capacity"/>
    /// commands, from the parked sets when one fits. The set comes back
    /// empty: its count is zero, so the commands its last owner wrote are
    /// outside everything the new owner's readers can see.</summary>
    internal OrderedDrawCommandBlock Take(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        int index = FirstFit(capacity);
        OrderedDrawCommandBlock block;
        if (index < _free.Count)
        {
            block = _free[index];
            _free.RemoveAt(index);
            _parkedCapacity -= block.Capacity;
            ReuseCount++;
        }
        else
        {
            block = new OrderedDrawCommandBlock();
            if (block.EnsureCapacity(capacity))
                AllocationCount++;
        }

        block.Count = 0;
        _liveCapacity += block.Capacity;
        if (_liveCapacity > _peakLiveCapacity)
            _peakLiveCapacity = _liveCapacity;
        return block;
    }

    /// <summary>Parks a set a producer no longer owns. A set with no arrays
    /// is nothing to keep.</summary>
    internal void Park(OrderedDrawCommandBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _liveCapacity -= block.Capacity;
        if (block.Capacity == 0)
            return;

        _free.Insert(FirstFit(block.Capacity), block);
        _parkedCapacity += block.Capacity;
        Trim();
    }

    /// <summary>Drops the smallest parked sets until what is parked fits
    /// inside one generation of live commands.</summary>
    private void Trim()
    {
        while (_parkedCapacity > _peakLiveCapacity && _free.Count > 0)
        {
            _parkedCapacity -= _free[0].Capacity;
            _free.RemoveAt(0);
        }
    }

    /// <summary>Index of the first parked set that holds
    /// <paramref name="capacity"/> commands, or the count when none does.</summary>
    private int FirstFit(int capacity)
    {
        int low = 0;
        int high = _free.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_free[middle].Capacity < capacity)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
