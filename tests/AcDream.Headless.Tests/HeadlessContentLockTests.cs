using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using AcDream.Content;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Runtime.Session;
using DatIDBObj = DatReaderWriter.Lib.IO.IDBObj;

namespace AcDream.Headless.Tests;

/// <summary>
/// The windowless host reads the installed data files under the one lock the
/// process hands out with every lease, the same lock a plugin's own reads are
/// made under. A host-side read made without it would race a plugin reading
/// the same files from its own thread, so every read the host makes is
/// watched for the lease's lock being held.
/// </summary>
public sealed class HeadlessContentLockTests
{
    [Fact]
    public void TheHostReadsItsTablesUnderTheLeasesLock()
    {
        var content = new WatchedContent();
        using var fixture = new HostFixture(content);

        // The character-creation tables are read while the host is built.
        int readsAtConstruction = content.Reads;
        Assert.True(readsAtConstruction > 0, "the host read nothing while it was built");
        Assert.Equal(0, content.ReadsOutsideTheHostLock);

        // The skill table is read when the character bindings are made.
        fixture.Host.CreateCharacterBindings();

        Assert.True(content.Reads > readsAtConstruction, "the skill table was not read");
        Assert.Equal(0, content.ReadsOutsideTheHostLock);
    }

    // ── Fixture ───────────────────────────────────────────────────────────

    private sealed class HostFixture : IDisposable
    {
        private readonly HeadlessCredentialSecret _credential;
        private readonly StringWriter _diagnostics = new();

        internal HostFixture(WatchedContent content)
        {
            Owner = new HeadlessProcessContentOwner(
                new HeadlessContentDescriptor
                {
                    DatDirectory = "fixture-dats",
                    PreparedAssetPath = "fixture.pak",
                },
                _ => { },
                new WatchedContentFactory(content));
            // The lock is known only once the owner exists; the reads made
            // while the host is built are the ones under test, so the watch
            // is armed before the host is.
            content.HostLock = Owner.DatLock;
            _credential = new HeadlessCredentialSecret("fixture", "password");
            Host = new HeadlessSessionHost(
                HeadlessSessionHostTests.Descriptor(),
                _credential,
                new HeadlessDiagnosticWriter(_diagnostics),
                new UnusedSessionOperations(),
                contentLease: Owner.AcquireLease("content-lock-fixture"));
        }

        internal HeadlessProcessContentOwner Owner { get; }

        internal HeadlessSessionHost Host { get; }

        public void Dispose()
        {
            Host.Dispose();
            Owner.Dispose();
            _credential.Dispose();
            _diagnostics.Dispose();
        }
    }

    private sealed class WatchedContentFactory(WatchedContent content)
        : IHeadlessProcessContentFactory
    {
        public HeadlessOpenedProcessContent Open(
            HeadlessContentDescriptor descriptor,
            Action<string> diagnostic) =>
            new(
                content,
                DispatchProxy.Create<ITestPreparedSource, TestResourceProxy>(),
                MagicCatalog.Empty,
                ImmutableArray.CreateRange(new float[256]));
    }

    /// <summary>
    /// Empty files behind the reader interface the lease lends, counting
    /// every read made without the host's lock held. A read made before the
    /// lock is known counts as outside it.
    /// </summary>
    private sealed class WatchedContent : IDatReaderWriter
    {
        public object? HostLock { get; set; }

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
            if (HostLock is null || !Monitor.IsEntered(HostLock))
                ReadsOutsideTheHostLock++;
            return default;
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

    /// <summary>
    /// The fixture never connects: the reads are made by the host itself,
    /// so nothing here is ever called.
    /// </summary>
    private sealed class UnusedSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            throw new NotSupportedException();

        public void Connect(
            WorldSession session, string user, string password) =>
            throw new NotSupportedException();

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            throw new NotSupportedException();

        public void EnterWorld(WorldSession session, int activeCharacterIndex) =>
            throw new NotSupportedException();

        public void Tick(WorldSession session) =>
            throw new NotSupportedException();

        public void DisposeSession(WorldSession session) => session.Dispose();
    }
}
