using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public class CellArrayTests
{
    [Fact]
    public void Add_PreservesInsertionOrder()
    {
        var a = new CellArray();
        a.Add(0xA9B40170u);
        a.Add(0xA9B40031u);
        a.Add(0xA9B40171u);
        Assert.Equal(new[] { 0xA9B40170u, 0xA9B40031u, 0xA9B40171u }, a.OrderedIds.ToArray());
    }

    [Fact]
    public void Add_DedupsById_KeepingFirstPosition()
    {
        var a = new CellArray();
        a.Add(0xA9B40170u);
        a.Add(0xA9B40171u);
        a.Add(0xA9B40170u);
        Assert.Equal(2, a.Count);
        Assert.Equal(new[] { 0xA9B40170u, 0xA9B40171u }, a.OrderedIds.ToArray());
    }

    [Fact]
    public void Contains_TracksMembership()
    {
        var a = new CellArray();
        a.Add(0xA9B40170u);
        Assert.Contains(0xA9B40170u, a);
        Assert.DoesNotContain(0xA9B40171u, a);
    }

    [Fact]
    public void EnumeratesInInsertionOrder_AsICollection()
    {
        var a = new CellArray();
        a.Add(3u); a.Add(1u); a.Add(2u);
        ICollection<uint> c = a;               // helper-facing interface
        Assert.Equal(new[] { 3u, 1u, 2u }, c.ToArray());
    }

    [Fact]
    public void IsReadOnlyCollection_ForConsumers()
    {
        var a = new CellArray();
        a.Add(7u); a.Add(7u);
        IReadOnlyCollection<uint> ro = a;
        int count = ro.Count;
        Assert.Equal(1, count);
        Assert.Equal(new[] { 7u }, ro.ToArray());
    }

    /// <summary>
    /// A cell array is walked several times per collision step, so the
    /// enumerator it hands a foreach must not be an allocation: the public
    /// one is the list's own, returned by value.
    /// </summary>
    [Fact]
    public void WalkingACellArrayDirectlyAllocatesNothing()
    {
        var cells = new CellArray();
        for (uint id = 1; id <= 64; id++)
            cells.Add(0xA9B40000u | id);

        ulong warm = Sum(cells);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong walked = Sum(cells);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(warm, walked);
        Assert.Equal(0L, allocated);
    }

    /// <summary>The direct walk is the same walk as the collection one.</summary>
    [Fact]
    public void WalkingACellArrayDirectlyMatchesWalkingItAsACollection()
    {
        var cells = new CellArray();
        cells.Add(0xA9B40170u);
        cells.Add(0xA9B40031u);
        cells.Add(0xA9B40171u);
        cells.Add(0xA9B40031u);

        var direct = new List<uint>();
        foreach (uint id in cells)
            direct.Add(id);

        var asCollection = new List<uint>();
        foreach (uint id in (IEnumerable<uint>)cells)
            asCollection.Add(id);

        Assert.Equal(new[] { 0xA9B40170u, 0xA9B40031u, 0xA9B40171u }, direct);
        Assert.Equal(direct, asCollection);
    }

    /// <summary>
    /// The cell-set search walks the ids in order for every sphere path a
    /// frame resolves; that walk takes no enumerator either.
    /// </summary>
    [Fact]
    public void WalkingACellArraysOrderedIdsAllocatesNothing()
    {
        var cells = new CellArray();
        for (uint id = 1; id <= 64; id++)
            cells.Add(0xA9B40000u | id);

        ulong warm = SumOrdered(cells);
        long before = GC.GetAllocatedBytesForCurrentThread();
        ulong walked = SumOrdered(cells);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(warm, walked);
        Assert.Equal(0L, allocated);
    }

    private static ulong Sum(CellArray cells)
    {
        ulong total = 0;
        foreach (uint id in cells)
            total += id;
        return total;
    }

    private static ulong SumOrdered(CellArray cells)
    {
        ulong total = 0;
        foreach (uint id in cells.OrderedIds)
            total += id;
        return total;
    }
}
