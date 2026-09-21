using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Maps;
using DatReaderWriter;
using DatIDBObj = DatReaderWriter.Lib.IO.IDBObj;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The binding pass reads the installed data files under the lock the host
/// handed it with them, never a lock of its own: the host's other readers
/// hold that one, and the files are not safe to read from two threads at
/// once. Every read the pass makes, at binding and later when a plugin asks
/// for a floorplan, is watched for the host's lock being held.
/// </summary>
public sealed class RuntimeAutomationContentLockTests
{
    [Fact]
    public void TheFloorplanIsReadUnderTheLockTheHostHandedOver()
    {
        var hostLock = new object();
        using var content = new WatchedContent(hostLock);
        using GameRuntime runtime = NewRuntime();
        using var surface = new RuntimeAutomationSurface();

        RuntimeAutomationBindings.Apply(surface, runtime, new RuntimeAutomationHostCapabilities
        {
            HostName = "a host under test",
            Declared = RuntimeAutomationHostCapabilities.AllCapabilityNames,
            Content = new RuntimeAutomationContent(content, hostLock),
        });
        int readsAtBinding = content.Reads;
        Assert.Equal(0, content.ReadsOutsideTheHostLock);

        PluginDungeonFloorplan plan = surface.DungeonMap.CaptureFloorplan(TwoRoomLandblock.Landblock);

        Assert.False(plan.IsEmpty);
        Assert.True(content.Reads > readsAtBinding, "the plan was not read from the files");
        Assert.Equal(0, content.ReadsOutsideTheHostLock);
    }

    private static GameRuntime NewRuntime() => GameRuntimeTestFactory.Create();

    /// <summary>
    /// The two-room landblock behind the reader interface a host lends,
    /// counting every read made without the host's lock held.
    /// </summary>
    private sealed class WatchedContent : IDatReaderWriter
    {
        private readonly TwoRoomLandblock _rooms = new();
        private readonly object _hostLock;

        public WatchedContent(object hostLock) => _hostLock = hostLock;

        public int Reads { get; private set; }
        public int ReadsOutsideTheHostLock { get; private set; }

        public string SourceDirectory => string.Empty;
        public IDatDatabase Portal => throw new NotSupportedException();
        public IDatDatabase Cell => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, IDatDatabase> CellRegions { get; } =
            new(new Dictionary<uint, IDatDatabase>());
        public IDatDatabase HighRes => throw new NotSupportedException();
        public IDatDatabase Language => throw new NotSupportedException();
        public IDatDatabase Local => throw new NotSupportedException();
        public ReadOnlyDictionary<uint, uint> RegionFileMap { get; } =
            new(new Dictionary<uint, uint>());
        public int PortalIteration => 0;
        public int CellIteration => 0;
        public int HighResIteration => 0;
        public int LanguageIteration => 0;

        public bool TryGetFileBytes(
            uint regionId, uint fileId, ref byte[] bytes, out int bytesRead)
        {
            bytesRead = 0;
            return false;
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : DatIDBObj =>
            Array.Empty<uint>();

        public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id) =>
            Array.Empty<IDatReaderWriter.IdResolution>();

        public bool TrySave<T>(T obj, int iteration = 0) where T : DatIDBObj =>
            throw new NotSupportedException();

        public bool TrySave<T>(uint regionId, T obj, int iteration = 0)
            where T : DatIDBObj => throw new NotSupportedException();

        [return: MaybeNull]
        public T Get<T>(uint fileId) where T : DatIDBObj
        {
            Reads++;
            if (!Monitor.IsEntered(_hostLock))
                ReadsOutsideTheHostLock++;
            // The rooms insist on their own lock; this reader's contract is
            // the host's, so the rooms are read under theirs on the way.
            lock (_rooms.Lock)
                return _rooms.Get<T>(fileId);
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : DatIDBObj
        {
            value = Get<T>(fileId);
            return value is not null;
        }

        public void Dispose()
        {
        }
    }
}
