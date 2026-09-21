using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using AcDream.Content;
using AcDream.Runtime.Maps;
using AcDream.Runtime.Tests.Plugins;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using Xunit.Abstractions;

namespace AcDream.Runtime.Tests.Maps;

/// <summary>
/// The floorplan builder on real dungeons from the installed game files,
/// drawn to files a person can look at. Not a pass/fail on the picture:
/// the assertions are that a plan comes out and has floor and walls; the
/// verdict on whether it reads as a floorplan is the reader's. Each run
/// writes <c>&lt;landblock&gt;.svg</c>, <c>&lt;landblock&gt;.png</c> and a
/// <c>stats.txt</c> to <c>ACDREAM_FLOORPLAN_OUT</c>, or to the temp folder.
/// <c>ACDREAM_FLOORPLAN_LANDBLOCKS</c> is a comma-separated list of hex
/// landblock ids to draw instead of the default three.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class DungeonFloorplanSpikeInstalledDatTests
{
    private static readonly uint[] DefaultLandblocks = [0x8A020000u, 0x01250000u, 0x00070000u];

    private readonly ITestOutputHelper _output;

    public DungeonFloorplanSpikeInstalledDatTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ListSealedDungeonsByCellCount()
    {
        using DatCollectionAdapter dats = OpenInstalled();
        var candidates = new List<(uint Landblock, uint Cells)>();
        // Every landblock in turn: the info file is one lookup and most miss.
        for (uint landblock = 0u; landblock <= 0xFFFFu; landblock++)
        {
            uint id = (landblock << 16) | 0xFFFEu;
            LandBlockInfo? info = dats.Get<LandBlockInfo>(id);
            if (info is null || info.NumCells == 0)
                continue;
            EnvCell? first = dats.Get<EnvCell>((id & 0xFFFF0000u) | 0x0100u);
            if (first is null || first.Flags.HasFlag(EnvCellFlags.SeenOutside))
                continue;
            candidates.Add((id & 0xFFFF0000u, info.NumCells));
        }

        candidates.Sort(static (a, b) => b.Cells.CompareTo(a.Cells));
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"sealed dungeons: {candidates.Count}");
        foreach ((uint landblock, uint cells) in candidates.Take(400))
            text.AppendLine(CultureInfo.InvariantCulture, $"0x{landblock:X8} cells={cells}");
        string path = Path.Combine(OutputDirectory(), "sealed-dungeons.txt");
        File.WriteAllText(path, text.ToString());
        _output.WriteLine($"wrote {path}");
        _output.WriteLine(text.ToString());
        Assert.NotEmpty(candidates);
    }

    [Fact]
    public void RealDungeonsAreDrawnToFiles()
    {
        using DatCollectionAdapter dats = OpenInstalled();
        var datLock = new object();
        var builder = new DungeonFloorplanBuilder(dats, datLock);
        string directory = OutputDirectory();
        var stats = new StringBuilder();

        foreach (uint landblock in Landblocks())
        {
            var stopwatch = Stopwatch.StartNew();
            DungeonFloorplan? plan = builder.TryBuild(landblock);
            stopwatch.Stop();
            Assert.NotNull(plan);

            string line =
                $"0x{landblock:X8}: cells={plan.Cells.Length} layers={plan.Layers.Length}"
                + $" {plan.Counts} bounds=({plan.Bounds.Min.X:F1},{plan.Bounds.Min.Y:F1},{plan.Bounds.Min.Z:F1})"
                + $"..({plan.Bounds.Max.X:F1},{plan.Bounds.Max.Y:F1},{plan.Bounds.Max.Z:F1})"
                + $" built in {stopwatch.Elapsed.TotalMilliseconds:F1} ms";
            foreach (DungeonFloorplanLayer layer in plan.Layers)
            {
                line += $"\n    layer z={layer.Z,6:F0}: floors={layer.Floors.Length,4} walls={layer.Walls.Length,4}"
                    + $" cells={plan.Cells.Count(cell => cell.LayerZ == layer.Z),3}";
            }
            stats.AppendLine(line);
            _output.WriteLine(line);

            File.WriteAllText(Path.Combine(directory, $"{landblock:X8}.svg"), Svg(plan));
            File.WriteAllBytes(Path.Combine(directory, $"{landblock:X8}.png"), Png(plan));
            Assert.True(plan.Counts.Floors > 0, "no floor at all");
            Assert.True(plan.Counts.WallsAfterMerge > 0, "no walls at all");
        }

        File.WriteAllText(Path.Combine(directory, "stats.txt"), stats.ToString());
        _output.WriteLine($"wrote {directory}");
    }

    private static uint[] Landblocks()
    {
        string? configured = System.Environment.GetEnvironmentVariable("ACDREAM_FLOORPLAN_LANDBLOCKS");
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultLandblocks;
        return configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static text => Convert.ToUInt32(text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16) & 0xFFFF0000u)
            .ToArray();
    }

    private static DatCollectionAdapter OpenInstalled()
    {
        string? datDirectory = InstalledDatTestPath.Resolve();
        if (datDirectory is null)
        {
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            throw new InvalidOperationException();
        }
        return new DatCollectionAdapter(new DatCollection(datDirectory, DatAccessType.Read));
    }

    private static string OutputDirectory()
    {
        string directory = System.Environment.GetEnvironmentVariable("ACDREAM_FLOORPLAN_OUT")
            ?? Path.Combine(Path.GetTempPath(), "acdream-floorplans");
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ── SVG: one panel per layer, side by side ──────────────────────────

    private const float Scale = 6f;
    private const float Margin = 12f;

    private static string Svg(DungeonFloorplan plan)
    {
        float width = plan.Bounds.Max.X - plan.Bounds.Min.X;
        float height = plan.Bounds.Max.Y - plan.Bounds.Min.Y;
        float panelWidth = width * Scale + Margin * 2f;
        float panelHeight = height * Scale + Margin * 2f + 16f;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{panelWidth * plan.Layers.Length:F0}\" height=\"{panelHeight:F0}\" style=\"background:#fff\">\n");
        for (int index = 0; index < plan.Layers.Length; index++)
        {
            DungeonFloorplanLayer layer = plan.Layers[index];
            float originX = index * panelWidth + Margin;
            string X(float x) => ((x - plan.Bounds.Min.X) * Scale + originX).ToString("F1", CultureInfo.InvariantCulture);
            string Y(float y) => ((plan.Bounds.Max.Y - y) * Scale + Margin + 16f).ToString("F1", CultureInfo.InvariantCulture);

            svg.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{originX:F0}\" y=\"12\" font-family=\"sans-serif\" font-size=\"12\">0x{plan.LandblockId:X8} layer z={layer.Z:F0} floors={layer.Floors.Length} walls={layer.Walls.Length}</text>\n");
            foreach (System.Collections.Immutable.ImmutableArray<Vector2> floor in layer.Floors)
            {
                svg.Append("<polygon fill=\"#9db7d5\" fill-opacity=\"0.5\" stroke=\"none\" points=\"");
                foreach (Vector2 vertex in floor)
                    svg.Append(X(vertex.X)).Append(',').Append(Y(vertex.Y)).Append(' ');
                svg.Append("\"/>\n");
            }
            foreach (DungeonFloorplanWall wall in layer.Walls)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"<line x1=\"{X(wall.Start.X)}\" y1=\"{Y(wall.Start.Y)}\" x2=\"{X(wall.End.X)}\" y2=\"{Y(wall.End.Y)}\" stroke=\"#222\" stroke-width=\"1.2\"/>\n");
            }
            foreach (DungeonFloorplanCell cell in plan.Cells)
            {
                if (cell.LayerZ != layer.Z)
                    continue;
                svg.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{X(cell.Center.X)}\" cy=\"{Y(cell.Center.Y)}\" r=\"2\" fill=\"#c33\"/>\n");
            }
        }
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    // ── PNG: the same picture, rasterised, so it can be looked at anywhere ──

    private static byte[] Png(DungeonFloorplan plan)
    {
        float width = plan.Bounds.Max.X - plan.Bounds.Min.X;
        float height = plan.Bounds.Max.Y - plan.Bounds.Min.Y;
        int panelWidth = (int)MathF.Ceiling(width * Scale + Margin * 2f);
        int panelHeight = (int)MathF.Ceiling(height * Scale + Margin * 2f);
        var canvas = new Canvas(panelWidth * plan.Layers.Length, panelHeight);
        for (int index = 0; index < plan.Layers.Length; index++)
        {
            DungeonFloorplanLayer layer = plan.Layers[index];
            float originX = index * panelWidth + Margin;
            Vector2 Map(Vector2 point) => new(
                (point.X - plan.Bounds.Min.X) * Scale + originX,
                (plan.Bounds.Max.Y - point.Y) * Scale + Margin);

            foreach (System.Collections.Immutable.ImmutableArray<Vector2> floor in layer.Floors)
                canvas.FillPolygon(floor.Select(Map).ToArray(), 0x9D, 0xB7, 0xD5);
            foreach (DungeonFloorplanWall wall in layer.Walls)
                canvas.Line(Map(wall.Start), Map(wall.End), 0x22, 0x22, 0x22);
            foreach (DungeonFloorplanCell cell in plan.Cells)
            {
                if (cell.LayerZ == layer.Z)
                    canvas.Dot(Map(new Vector2(cell.Center.X, cell.Center.Y)), 0xCC, 0x33, 0x33);
            }
            // A frame between panels.
            canvas.Line(new Vector2(index * panelWidth, 0), new Vector2(index * panelWidth, panelHeight - 1), 0xBB, 0xBB, 0xBB);
        }
        return canvas.Encode();
    }

    private sealed class Canvas
    {
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _pixels;

        public Canvas(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _pixels = new byte[_width * _height * 3];
            Array.Fill(_pixels, (byte)0xFF);
        }

        private void Set(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= _width || y >= _height)
                return;
            int offset = (y * _width + x) * 3;
            _pixels[offset] = r;
            _pixels[offset + 1] = g;
            _pixels[offset + 2] = b;
        }

        public void Dot(Vector2 at, byte r, byte g, byte b)
        {
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    Set((int)at.X + dx, (int)at.Y + dy, r, g, b);
        }

        public void Line(Vector2 from, Vector2 to, byte r, byte g, byte b)
        {
            int x0 = (int)MathF.Round(from.X), y0 = (int)MathF.Round(from.Y);
            int x1 = (int)MathF.Round(to.X), y1 = (int)MathF.Round(to.Y);
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                Set(x0, y0, r, g, b);
                if (x0 == x1 && y0 == y1)
                    break;
                int doubled = 2 * error;
                if (doubled >= dy) { error += dy; x0 += sx; }
                if (doubled <= dx) { error += dx; y0 += sy; }
            }
        }

        /// <summary>Even-odd scanline fill, so a bent polygon fills the same way an SVG polygon does.</summary>
        public void FillPolygon(Vector2[] polygon, byte r, byte g, byte b)
        {
            if (polygon.Length < 3)
                return;
            float minY = polygon.Min(static p => p.Y), maxY = polygon.Max(static p => p.Y);
            var crossings = new List<float>();
            for (int y = Math.Max(0, (int)MathF.Floor(minY)); y <= Math.Min(_height - 1, (int)MathF.Ceiling(maxY)); y++)
            {
                float scan = y + 0.5f;
                crossings.Clear();
                for (int index = 0; index < polygon.Length; index++)
                {
                    Vector2 a = polygon[index];
                    Vector2 c = polygon[(index + 1) % polygon.Length];
                    if ((a.Y <= scan) == (c.Y <= scan))
                        continue;
                    crossings.Add(a.X + (scan - a.Y) / (c.Y - a.Y) * (c.X - a.X));
                }
                crossings.Sort();
                for (int index = 0; index + 1 < crossings.Count; index += 2)
                {
                    for (int x = (int)MathF.Round(crossings[index]); x < (int)MathF.Round(crossings[index + 1]); x++)
                        Set(x, y, r, g, b);
                }
            }
        }

        public byte[] Encode()
        {
            using var output = new MemoryStream();
            output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, _width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], _height);
            header[8] = 8;
            header[9] = 2;
            Chunk(output, "IHDR", header.ToArray());

            using var raw = new MemoryStream();
            using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = _width * 3;
                for (int y = 0; y < _height; y++)
                {
                    zlib.WriteByte(0);
                    zlib.Write(_pixels, y * stride, stride);
                }
            }
            Chunk(output, "IDAT", raw.ToArray());
            Chunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        private static void Chunk(Stream output, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes);
            output.Write(data);
            uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
            crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            output.Write(crcBytes);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] data, uint crc)
        {
            foreach (byte value in data)
                crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
