using AcDream.Content.Navigation;
using AcDream.Content.Pak;

namespace AcDream.Content;

public readonly record struct PreparedNavigationSourceStats(
    long Probes,
    long Reads,
    long Loaded,
    long Missing,
    long Corrupt);

public readonly record struct PreparedNavigationReadResult(
    PreparedAssetReadStatus Status,
    PreparedNavigationTileSet? Data)
{
    public static PreparedNavigationReadResult Missing =>
        new(PreparedAssetReadStatus.Missing, null);

    public static PreparedNavigationReadResult Corrupt =>
        new(PreparedAssetReadStatus.Corrupt, null);

    public static PreparedNavigationReadResult Loaded(
        PreparedNavigationTileSet data) =>
        new(PreparedAssetReadStatus.Loaded, data);
}

public interface IPreparedNavigationSource : IDisposable
{
    PreparedAssetPresence ProbeNavigation(uint canonicalLandblockId);

    PreparedNavigationReadResult ReadNavigation(
        uint canonicalLandblockId,
        CancellationToken cancellationToken = default);

    PreparedNavigationSourceStats NavigationStats { get; }
}

public sealed partial class PakPreparedAssetSource
{
    private long _navigationProbes;
    private long _navigationReads;
    private long _navigationLoaded;
    private long _navigationMissing;
    private long _navigationCorrupt;

    public PreparedNavigationSourceStats NavigationStats =>
        new(
            Volatile.Read(ref _navigationProbes),
            Volatile.Read(ref _navigationReads),
            Volatile.Read(ref _navigationLoaded),
            Volatile.Read(ref _navigationMissing),
            Volatile.Read(ref _navigationCorrupt));

    public PreparedAssetPresence ProbeNavigation(uint canonicalLandblockId)
    {
        NavigationLandblockContract.Validate(canonicalLandblockId);
        Interlocked.Increment(ref _navigationProbes);
        ulong key = PakKey.Compose(
            PakAssetType.NavigationTile,
            canonicalLandblockId);
        return _reader.ProbeEntry(key) switch
        {
            PakEntryState.Available => PreparedAssetPresence.Available,
            PakEntryState.Corrupt => PreparedAssetPresence.Corrupt,
            _ => PreparedAssetPresence.Missing,
        };
    }

    public PreparedNavigationReadResult ReadNavigation(
        uint canonicalLandblockId,
        CancellationToken cancellationToken = default)
    {
        NavigationLandblockContract.Validate(canonicalLandblockId);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _navigationReads);

        ulong key = PakKey.Compose(
            PakAssetType.NavigationTile,
            canonicalLandblockId);
        PakObjectReadStatus status =
            _reader.ReadBlobBytes(key, out byte[]? bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (status == PakObjectReadStatus.Missing)
        {
            Interlocked.Increment(ref _navigationMissing);
            return PreparedNavigationReadResult.Missing;
        }
        if (status == PakObjectReadStatus.Corrupt || bytes is null)
        {
            Interlocked.Increment(ref _navigationCorrupt);
            return PreparedNavigationReadResult.Corrupt;
        }

        try
        {
            PreparedNavigationTileSet tileSet =
                PreparedNavigationTileCodec.Deserialize(
                    bytes,
                    cancellationToken);
            if (tileSet.CanonicalLandblockId != canonicalLandblockId)
            {
                throw new InvalidDataException(
                    $"navigation payload identity 0x{tileSet.CanonicalLandblockId:X8} " +
                    $"does not match key identity 0x{canonicalLandblockId:X8}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _navigationLoaded);
            return PreparedNavigationReadResult.Loaded(tileSet);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _reader.MarkPayloadCorrupt(
                key,
                $"navigation deserialization failed despite matching CRC: " +
                $"{exception.GetType().Name}: {exception.Message}");
            Interlocked.Increment(ref _navigationCorrupt);
            return PreparedNavigationReadResult.Corrupt;
        }
    }
}
