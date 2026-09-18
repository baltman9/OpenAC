using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.Enums;
using DatReaderWriter.Lib.IO;

namespace AcDream.App.Streaming;

/// <summary>
/// Serialises every content read against one gate, one read at a time.
///
/// The content reader is shared between the frame thread and the streaming
/// worker and is not safe to enter from two threads at once, so every read has
/// to hold the gate. Holding it around a whole landblock build made the frame
/// thread wait for the build: a creation message arriving mid-build blocked
/// for as long as the build ran. Holding it around each read instead keeps the
/// same rule -- no read outside the gate -- and bounds the wait at one record.
///
/// The content is immutable, so two readers of different records never race
/// logically and a build reads exactly what it read before, in the same order.
/// </summary>
internal sealed class LockedContentReads : IDatReaderWriter
{
    private readonly IDatReaderWriter _inner;
    private readonly object _gate;
    // Keyed by identity: two databases are the same database only when they
    // are the same object, whatever equality the implementation defines.
    private readonly Dictionary<IDatDatabase, LockedDatabase> _databases =
        new(DatabaseIdentity.Instance);

    internal LockedContentReads(IDatReaderWriter inner, object gate)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>The gate every read of this source takes.</summary>
    internal object Gate => _gate;

    public string SourceDirectory => _inner.SourceDirectory;

    public IDatDatabase Portal => Wrap(_inner.Portal);

    public IDatDatabase Cell => Wrap(_inner.Cell);

    public IDatDatabase HighRes => Wrap(_inner.HighRes);

    public IDatDatabase Language => Wrap(_inner.Language);

    public IDatDatabase Local => Wrap(_inner.Local);

    public ReadOnlyDictionary<uint, IDatDatabase> CellRegions
    {
        get
        {
            ReadOnlyDictionary<uint, IDatDatabase> regions = _inner.CellRegions;
            var wrapped = new Dictionary<uint, IDatDatabase>(regions.Count);
            foreach (KeyValuePair<uint, IDatDatabase> region in regions)
                wrapped[region.Key] = Wrap(region.Value);
            return new ReadOnlyDictionary<uint, IDatDatabase>(wrapped);
        }
    }

    public ReadOnlyDictionary<uint, uint> RegionFileMap
    {
        get { lock (_gate) return _inner.RegionFileMap; }
    }

    public int PortalIteration
    {
        get { lock (_gate) return _inner.PortalIteration; }
    }

    public int CellIteration
    {
        get { lock (_gate) return _inner.CellIteration; }
    }

    public int HighResIteration
    {
        get { lock (_gate) return _inner.HighResIteration; }
    }

    public int LanguageIteration
    {
        get { lock (_gate) return _inner.LanguageIteration; }
    }

    [return: MaybeNull]
    public T Get<T>(uint fileId) where T : IDBObj
    {
        lock (_gate) return _inner.Get<T>(fileId);
    }

    public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
        where T : IDBObj
    {
        lock (_gate) return _inner.TryGet(fileId, out value);
    }

    // Materialised inside the gate: the underlying enumerator reads as it is
    // walked, and a lazy sequence would be walked after the gate is released.
    public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj
    {
        lock (_gate) return _inner.GetAllIdsOfType<T>().ToList();
    }

    public bool TryGetFileBytes(
        uint regionId,
        uint fileId,
        ref byte[] bytes,
        out int bytesRead)
    {
        lock (_gate) return _inner.TryGetFileBytes(regionId, fileId, ref bytes, out bytesRead);
    }

    public IEnumerable<IDatReaderWriter.IdResolution> ResolveId(uint id)
    {
        lock (_gate)
        {
            var resolutions = new List<IDatReaderWriter.IdResolution>();
            foreach (IDatReaderWriter.IdResolution resolution in _inner.ResolveId(id))
            {
                resolutions.Add(
                    resolution with { Database = Wrap(resolution.Database) });
            }
            return resolutions;
        }
    }

    public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
        throw new NotSupportedException("The streaming content source is read-only.");

    public bool TrySave<T>(uint regionId, T obj, int iteration = 0) where T : IDBObj =>
        throw new NotSupportedException("The streaming content source is read-only.");

    // The decorated source is owned by whoever created it.
    public void Dispose()
    {
    }

    private LockedDatabase Wrap(IDatDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        lock (_databases)
        {
            if (!_databases.TryGetValue(database, out LockedDatabase? wrapped))
            {
                wrapped = new LockedDatabase(database, _gate);
                _databases.Add(database, wrapped);
            }

            return wrapped;
        }
    }

    private sealed class DatabaseIdentity : IEqualityComparer<IDatDatabase>
    {
        internal static readonly DatabaseIdentity Instance = new();

        public bool Equals(IDatDatabase? x, IDatDatabase? y) => ReferenceEquals(x, y);

        public int GetHashCode(IDatDatabase obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class LockedDatabase : IDatDatabase
    {
        private readonly IDatDatabase _inner;
        private readonly object _gate;

        internal LockedDatabase(IDatDatabase inner, object gate)
        {
            _inner = inner;
            _gate = gate;
        }

        public DatDatabase Db => _inner.Db;

        public int Iteration
        {
            get { lock (_gate) return _inner.Iteration; }
        }

        public IEnumerable<uint> GetAllIdsOfType<T>() where T : IDBObj
        {
            lock (_gate) return _inner.GetAllIdsOfType<T>().ToList();
        }

        public bool TryGet<T>(uint fileId, [MaybeNullWhen(false)] out T value)
            where T : IDBObj
        {
            lock (_gate) return _inner.TryGet(fileId, out value);
        }

        public bool TryGetFileBytes(
            uint fileId,
            [MaybeNullWhen(false)] out byte[] value)
        {
            lock (_gate) return _inner.TryGetFileBytes(fileId, out value);
        }

        public bool TryGetFileBytes(uint fileId, ref byte[] bytes, out int bytesRead)
        {
            lock (_gate) return _inner.TryGetFileBytes(fileId, ref bytes, out bytesRead);
        }

        public bool TrySave<T>(T obj, int iteration = 0) where T : IDBObj =>
            throw new NotSupportedException("The streaming content source is read-only.");

        public void Dispose()
        {
        }
    }
}
