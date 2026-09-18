using System.Collections;
using System.Collections.Generic;

namespace AcDream.Core.Physics;

public sealed class CellArray : ICollection<uint>, IReadOnlyCollection<uint>
{
    private readonly List<uint> _order = new();
    private readonly HashSet<uint> _seen = new();

    internal CellArray? UnionTarget { get; set; }

    public int Count => _order.Count;
    public bool IsReadOnly => false;

    /// <summary>
    /// The ids in the order they were added, as the list itself: the cell-set
    /// search walks this for every sphere path a frame resolves, and walking
    /// it through an interface boxed the list's enumerator every time. The
    /// list is the array's own; it is added to through <see cref="Add"/>.
    /// </summary>
    public List<uint> OrderedIds => _order;

    public void Add(uint id)
    {
        if (UnionTarget is { } target
            && !ReferenceEquals(target, this))
        {
            target.Add(id);
        }
        if (_seen.Add(id))
            _order.Add(id);
    }

    public bool Contains(uint id) => _seen.Contains(id);

    public void Clear() { _order.Clear(); _seen.Clear(); }

    public bool Remove(uint id)
    {
        if (!_seen.Remove(id)) return false;
        _order.Remove(id);
        return true;
    }

    public void CopyTo(uint[] array, int arrayIndex) => _order.CopyTo(array, arrayIndex);

    /// <summary>
    /// Walks the ids in the order they were added. The enumerator is the
    /// list's own, returned by value: a cell array is walked several times
    /// per collision step, and handing it back through the interface boxed
    /// one enumerator every time. The interface implementations below still
    /// box, for callers that hold a cell array as a collection.
    /// </summary>
    public List<uint>.Enumerator GetEnumerator() => _order.GetEnumerator();

    IEnumerator<uint> IEnumerable<uint>.GetEnumerator() => _order.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _order.GetEnumerator();
}
