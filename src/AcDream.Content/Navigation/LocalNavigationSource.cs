using System.Security.Cryptography;
using System.Text;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using AcDream.Content.Pak;

namespace AcDream.Content.Navigation;

/// <summary>Validated local tiles over a borrowed prepared source.</summary>
public sealed class LocalNavigationSource(string directory, IPreparedNavigationSource? fallback)
    : IPreparedNavigationSource
{
    public PreparedNavigationSourceStats NavigationStats => default;
    private string TilePath(uint id)
    {
        NavigationLandblockContract.Validate(id);
        return Path.Combine(directory, $"{id:X8}.navtile");
    }
    public PreparedAssetPresence ProbeNavigation(uint id) => File.Exists(TilePath(id))
        ? PreparedAssetPresence.Available : fallback?.ProbeNavigation(id) ?? PreparedAssetPresence.Missing;
    public PreparedNavigationReadResult ReadNavigation(uint id, CancellationToken cancellationToken = default)
    {
        string path = TilePath(id);
        if (!File.Exists(path)) return fallback?.ReadNavigation(id, cancellationToken) ?? PreparedNavigationReadResult.Missing;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var tile = PreparedNavigationTileCodec.Deserialize(File.ReadAllBytes(path), cancellationToken);
            if (tile.CanonicalLandblockId != id) return PreparedNavigationReadResult.Corrupt;
            return PreparedNavigationReadResult.Loaded(tile);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or OverflowException)
        { return PreparedNavigationReadResult.Corrupt; }
    }
    public void Dispose() { } // The fallback belongs to the host.

    public static string CacheDirectory(string datDirectory)
    {
        string identity = Path.GetFullPath(datDirectory) + "|navigation-1|" +
            string.Join('|', Directory.EnumerateFiles(datDirectory, "*.dat").Order()
                .Select(path => { var file = new FileInfo(path); return $"{file.Name}:{file.Length}:{file.LastWriteTimeUtc.Ticks}"; }));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "acdream", "navigation", key);
    }

    public static void GenerateNearby(string datDirectory, string cacheDirectory, uint center,
        Action<int, int> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDirectory);
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);
        using var source = new DatCollectionAdapter(dats);
        using var collisions = new DatPreparedCollisionSource(source);
        var region = source.Get<Region>(0x13000000u)
            ?? throw new InvalidDataException("Terrain height table is unavailable.");
        var available = dats.Cell.Tree.Where(file => (file.Id & 0xFFFFu) == 0xFFFFu)
            .Select(file => file.Id & 0xFFFF0000u).ToHashSet();
        uint[] targets = NavigationDatGeometry.GetHaloIds(center).Where(available.Contains).ToArray();
        if (!available.Contains(center)) throw new InvalidDataException("Current landblock is not in the installed terrain data.");
        progress(0, targets.Length);
        int complete = 0;
        foreach (uint id in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = NavigationDatGeometry.GetHaloIds(id).Where(available.Contains)
                .Select(halo => NavigationDatGeometry.PrepareLandblock(source, collisions,
                    halo, region.LandDefs.LandHeightTable, cancellationToken)).ToArray();
            var geometry = NavigationGeometryBuilder.Build(id, inputs, cancellationToken);
            var tile = NavigationTileBuilder.Build(geometry, cancellationToken: cancellationToken);
            byte[] bytes = PreparedNavigationTileCodec.Serialize(tile);
            _ = PreparedNavigationTileCodec.Deserialize(bytes, cancellationToken);
            string destination = Path.Combine(cacheDirectory, $"{id:X8}.navtile");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            progress(++complete, targets.Length);
        }
    }
}
