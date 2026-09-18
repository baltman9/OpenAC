using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace AcDream.Content.Navigation;

public enum NavigationPartitionType
{
    Watershed = 1,
    Monotone = 2,
    Layers = 3,
}

public readonly record struct PreparedNavigationBuildParameters(
    float CellSize,
    float CellHeight,
    float AgentHeight,
    float AgentRadius,
    float AgentMaxClimb,
    float AgentMaxSlopeDegrees,
    int RegionMinSize,
    int RegionMergeSize,
    float EdgeMaxLength,
    float EdgeMaxError,
    int VerticesPerPolygon,
    float DetailSampleDistance,
    float DetailSampleMaxError,
    int TileSizeCells,
    int BorderSizeCells,
    NavigationPartitionType PartitionType)
{
    public void Validate()
    {
        RequirePositive(CellSize, nameof(CellSize));
        RequirePositive(CellHeight, nameof(CellHeight));
        RequirePositive(AgentHeight, nameof(AgentHeight));
        RequirePositive(AgentRadius, nameof(AgentRadius));
        RequireNonNegative(AgentMaxClimb, nameof(AgentMaxClimb));
        if (!float.IsFinite(AgentMaxSlopeDegrees)
            || AgentMaxSlopeDegrees <= 0
            || AgentMaxSlopeDegrees >= 90)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AgentMaxSlopeDegrees));
        }

        if (RegionMinSize < 0)
            throw new ArgumentOutOfRangeException(nameof(RegionMinSize));
        if (RegionMergeSize < 0)
            throw new ArgumentOutOfRangeException(nameof(RegionMergeSize));
        RequireNonNegative(EdgeMaxLength, nameof(EdgeMaxLength));
        RequirePositive(EdgeMaxError, nameof(EdgeMaxError));
        if (VerticesPerPolygon is < 3 or > 12)
            throw new ArgumentOutOfRangeException(nameof(VerticesPerPolygon));
        RequireNonNegative(DetailSampleDistance, nameof(DetailSampleDistance));
        RequireNonNegative(DetailSampleMaxError, nameof(DetailSampleMaxError));
        if (TileSizeCells is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(TileSizeCells));
        if (BorderSizeCells < 0 || BorderSizeCells > TileSizeCells)
            throw new ArgumentOutOfRangeException(nameof(BorderSizeCells));
        if (!Enum.IsDefined(PartitionType))
            throw new ArgumentOutOfRangeException(nameof(PartitionType));

        float tileWorldSize = CellSize * TileSizeCells;
        RequirePositive(tileWorldSize, nameof(TileSizeCells));
        float tilesPerLandblock = 192f / tileWorldSize;
        int roundedTiles = checked((int)MathF.Round(tilesPerLandblock));
        if (roundedTiles is < 1 or > 64
            || MathF.Abs(tilesPerLandblock - roundedTiles) > 0.0001f)
        {
            throw new ArgumentException(
                "cell size and tile size must divide a 192-unit landblock " +
                "into a whole number of tiles");
        }
    }

    private static void RequirePositive(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void RequireNonNegative(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed class PreparedNavigationTileBlob
{
    private readonly byte[] _data;

    public PreparedNavigationTileBlob(ReadOnlyMemory<byte> data)
        : this(data.ToArray(), takeOwnership: true)
    {
    }

    private PreparedNavigationTileBlob(byte[] data, bool takeOwnership)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new ArgumentException("a navigation tile blob cannot be empty", nameof(data));
        if (data.Length > PreparedNavigationTileCodec.MaxTileBlobBytes)
            throw new ArgumentException("a navigation tile blob exceeds the format limit", nameof(data));

        _data = takeOwnership ? data : data.ToArray();
    }

    /// <summary>
    /// One complete little-endian Detour mesh-data payload written without
    /// C-compatibility padding.
    /// </summary>
    public ReadOnlyMemory<byte> Data => _data;

    internal static PreparedNavigationTileBlob FromOwned(byte[] data) =>
        new(data, takeOwnership: true);
}

public sealed class PreparedNavigationTileSet
{
    private readonly ReadOnlyCollection<PreparedNavigationTileBlob> _tiles;

    public PreparedNavigationTileSet(
        uint canonicalLandblockId,
        PreparedNavigationBuildParameters parameters,
        IEnumerable<ReadOnlyMemory<byte>> tiles)
    {
        NavigationLandblockContract.Validate(canonicalLandblockId);
        parameters.Validate();
        ArgumentNullException.ThrowIfNull(tiles);

        PreparedNavigationTileBlob[] tileArray = tiles
            .Select(static data => new PreparedNavigationTileBlob(data))
            .ToArray();
        PreparedNavigationTileCodec.ValidateTileCount(tileArray.Length);

        CanonicalLandblockId = canonicalLandblockId;
        Parameters = parameters;
        _tiles = Array.AsReadOnly(tileArray);
    }

    private PreparedNavigationTileSet(
        uint canonicalLandblockId,
        PreparedNavigationBuildParameters parameters,
        PreparedNavigationTileBlob[] tiles)
    {
        CanonicalLandblockId = canonicalLandblockId;
        Parameters = parameters;
        _tiles = Array.AsReadOnly(tiles);
    }

    public uint CanonicalLandblockId { get; }

    public PreparedNavigationBuildParameters Parameters { get; }

    public IReadOnlyList<PreparedNavigationTileBlob> Tiles => _tiles;

    internal static PreparedNavigationTileSet FromOwned(
        uint canonicalLandblockId,
        PreparedNavigationBuildParameters parameters,
        PreparedNavigationTileBlob[] tiles) =>
        new(canonicalLandblockId, parameters, tiles);
}

public static class PreparedNavigationTileCodec
{
    private const uint Magic = 0x564E_4341u;
    private const ushort SchemaVersion = 1;
    private const ushort HeaderSize = 80;

    internal const int MaxTileBlobBytes = 64 * 1024 * 1024;
    private const int MaxTileCount = 256;
    private const int MaxEnvelopeBytes = 512 * 1024 * 1024;

    public static byte[] Serialize(PreparedNavigationTileSet tileSet)
    {
        ArgumentNullException.ThrowIfNull(tileSet);
        NavigationLandblockContract.Validate(tileSet.CanonicalLandblockId);
        tileSet.Parameters.Validate();
        ValidateTileCount(tileSet.Tiles.Count);

        int length = HeaderSize;
        foreach (PreparedNavigationTileBlob tile in tileSet.Tiles)
        {
            int tileLength = tile.Data.Length;
            if (tileLength is < 1 or > MaxTileBlobBytes)
                throw new InvalidDataException("navigation tile blob length is invalid");
            length = checked(length + sizeof(int) + tileLength);
            if (length > MaxEnvelopeBytes)
                throw new InvalidDataException("navigation tile envelope exceeds the format limit");
        }

        byte[] bytes = new byte[length];
        Span<byte> destination = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], SchemaVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[8..12],
            tileSet.CanonicalLandblockId);
        WriteParameters(destination[12..76], tileSet.Parameters);
        BinaryPrimitives.WriteInt32LittleEndian(destination[76..80], tileSet.Tiles.Count);

        int offset = HeaderSize;
        foreach (PreparedNavigationTileBlob tile in tileSet.Tiles)
        {
            ReadOnlySpan<byte> tileBytes = tile.Data.Span;
            BinaryPrimitives.WriteInt32LittleEndian(
                destination.Slice(offset, sizeof(int)),
                tileBytes.Length);
            offset += sizeof(int);
            tileBytes.CopyTo(destination[offset..]);
            offset += tileBytes.Length;
        }

        return bytes;
    }

    public static PreparedNavigationTileSet Deserialize(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length < HeaderSize || bytes.Length > MaxEnvelopeBytes)
            throw new InvalidDataException("navigation tile envelope length is invalid");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[0..4]) != Magic)
            throw new InvalidDataException("navigation tile envelope magic mismatch");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]) != SchemaVersion)
            throw new InvalidDataException("unsupported navigation tile schema version");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]) != HeaderSize)
            throw new InvalidDataException("navigation tile header size mismatch");

        uint canonicalLandblockId =
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
        NavigationLandblockContract.Validate(canonicalLandblockId);
        PreparedNavigationBuildParameters parameters = ReadParameters(bytes[12..76]);
        parameters.Validate();
        int tileCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[76..80]);
        ValidateTileCount(tileCount);

        var tiles = new PreparedNavigationTileBlob[tileCount];
        int offset = HeaderSize;
        for (int index = 0; index < tileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes.Length - offset < sizeof(int))
                throw new InvalidDataException("navigation tile length is truncated");
            int tileLength = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(offset, sizeof(int)));
            offset += sizeof(int);
            if (tileLength is < 1 or > MaxTileBlobBytes)
                throw new InvalidDataException("navigation tile blob length is invalid");
            if (tileLength > bytes.Length - offset)
                throw new InvalidDataException("navigation tile blob is truncated");

            tiles[index] = PreparedNavigationTileBlob.FromOwned(
                bytes.Slice(offset, tileLength).ToArray());
            offset += tileLength;
        }

        if (offset != bytes.Length)
            throw new InvalidDataException("navigation tile envelope has trailing bytes");

        return PreparedNavigationTileSet.FromOwned(
            canonicalLandblockId,
            parameters,
            tiles);
    }

    internal static void ValidateTileCount(int tileCount)
    {
        if (tileCount is < 0 or > MaxTileCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tileCount),
                tileCount,
                "a navigation tile envelope must contain 0 to 256 tiles");
        }
    }

    private static void WriteParameters(
        Span<byte> destination,
        PreparedNavigationBuildParameters parameters)
    {
        WriteSingle(destination[0..4], parameters.CellSize);
        WriteSingle(destination[4..8], parameters.CellHeight);
        WriteSingle(destination[8..12], parameters.AgentHeight);
        WriteSingle(destination[12..16], parameters.AgentRadius);
        WriteSingle(destination[16..20], parameters.AgentMaxClimb);
        WriteSingle(destination[20..24], parameters.AgentMaxSlopeDegrees);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..28], parameters.RegionMinSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..32], parameters.RegionMergeSize);
        WriteSingle(destination[32..36], parameters.EdgeMaxLength);
        WriteSingle(destination[36..40], parameters.EdgeMaxError);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..44], parameters.VerticesPerPolygon);
        WriteSingle(destination[44..48], parameters.DetailSampleDistance);
        WriteSingle(destination[48..52], parameters.DetailSampleMaxError);
        BinaryPrimitives.WriteInt32LittleEndian(destination[52..56], parameters.TileSizeCells);
        BinaryPrimitives.WriteInt32LittleEndian(destination[56..60], parameters.BorderSizeCells);
        BinaryPrimitives.WriteInt32LittleEndian(destination[60..64], (int)parameters.PartitionType);
    }

    private static PreparedNavigationBuildParameters ReadParameters(
        ReadOnlySpan<byte> source) =>
        new(
            ReadSingle(source[0..4]),
            ReadSingle(source[4..8]),
            ReadSingle(source[8..12]),
            ReadSingle(source[12..16]),
            ReadSingle(source[16..20]),
            ReadSingle(source[20..24]),
            BinaryPrimitives.ReadInt32LittleEndian(source[24..28]),
            BinaryPrimitives.ReadInt32LittleEndian(source[28..32]),
            ReadSingle(source[32..36]),
            ReadSingle(source[36..40]),
            BinaryPrimitives.ReadInt32LittleEndian(source[40..44]),
            ReadSingle(source[44..48]),
            ReadSingle(source[48..52]),
            BinaryPrimitives.ReadInt32LittleEndian(source[52..56]),
            BinaryPrimitives.ReadInt32LittleEndian(source[56..60]),
            (NavigationPartitionType)BinaryPrimitives.ReadInt32LittleEndian(source[60..64]));

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(source));
}
