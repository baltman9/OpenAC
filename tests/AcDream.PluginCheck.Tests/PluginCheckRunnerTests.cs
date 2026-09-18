using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AcDream.PluginCheck;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.PluginCheck.Tests;

public sealed class PluginCheckRunnerTests : IDisposable
{
    private const string Id = "acme.hello";
    private const string EntryDll = "acme.hello.dll";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "acdream-plugincheck-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AValidDirectoryPasses()
    {
        string directory = WriteValidPluginFolder("valid-directory");

        PluginCheckReport report = await PluginCheckRunner.RunAsync(directory);

        Assert.Equal(PluginCheckMode.Directory, report.Mode);
        Assert.Equal(PluginCheckVerdict.WouldInstall, report.Verdict);
        Assert.All(report.Checks, check => Assert.NotEqual(PluginCheckStatus.Fail, check.Status));
    }

    [Fact]
    public async Task AValidZipPasses()
    {
        string zipPath = WriteValidPluginZip("valid-zip", ValidManifestJson());

        PluginCheckReport report = await PluginCheckRunner.RunAsync(zipPath);

        Assert.Equal(PluginCheckMode.Zip, report.Mode);
        Assert.Equal(PluginCheckVerdict.WouldInstall, report.Verdict);
        Assert.All(report.Checks, check => Assert.NotEqual(PluginCheckStatus.Fail, check.Status));
    }

    [Fact]
    public async Task BadManifestJsonFails()
    {
        string directory = Path.Combine(_root, "bad-json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), "{ not json");
        File.WriteAllBytes(Path.Combine(directory, EntryDll), [1]);

        PluginCheckReport report = await PluginCheckRunner.RunAsync(directory);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(report.Checks, c => c.Check == "plugin.json parses");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
        Assert.Contains("invalid json", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnInvalidIdFails()
    {
        string directory = WriteValidPluginFolder(
            "bad-id", ValidManifestJson(id: "Acme.Hello"));

        PluginCheckReport report = await PluginCheckRunner.RunAsync(directory);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(
            report.Checks, c => c.Check == "manifest satisfies install rules");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
        Assert.Contains("namespaced pattern", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidVersionFails()
    {
        string directory = WriteValidPluginFolder(
            "bad-version", ValidManifestJson(version: "not-a-version"));

        PluginCheckReport report = await PluginCheckRunner.RunAsync(directory);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(
            report.Checks, c => c.Check == "manifest satisfies install rules");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
        Assert.Contains("SemVer", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedIconFailsInDirectoryMode()
    {
        string directory = WriteValidPluginFolder("oversized-icon");
        File.WriteAllBytes(
            Path.Combine(directory, "icon.png"),
            PngTestData.OverMaximumBytes());

        PluginCheckReport report = await PluginCheckRunner.RunAsync(directory);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(
            report.Checks, c => c.Check == "folder passes the direct-install checks");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
        Assert.Contains("larger than", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongDimensionIconFailsInZipMode()
    {
        string zipPath = WriteValidPluginZip(
            "wrong-dimensions",
            ValidManifestJson(),
            icon: PngTestData.WrongDimensions());

        PluginCheckReport report = await PluginCheckRunner.RunAsync(zipPath);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(report.Checks, c => c.Check == "plugin icon");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
        Assert.Contains("64x64", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJpegIconAtRootFailsInZipMode()
    {
        string zipPath = Path.Combine(_root, "jpeg-icon.zip");
        Directory.CreateDirectory(_root);
        using (FileStream stream = File.Create(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddEntry(archive, "plugin.json", Encoding.UTF8.GetBytes(ValidManifestJson()));
            AddEntry(archive, EntryDll, [1, 2, 3]);
            AddEntry(archive, "icon.jpg", [1, 2, 3]);
        }

        PluginCheckReport report = await PluginCheckRunner.RunAsync(zipPath);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(report.Checks, c => c.Check == "plugin content policy");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
        Assert.Contains("JPEG", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsafeZipEntryFails()
    {
        string zipPath = Path.Combine(_root, "path-traversal.zip");
        Directory.CreateDirectory(_root);
        using (FileStream stream = File.Create(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddEntry(archive, "plugin.json", Encoding.UTF8.GetBytes(ValidManifestJson()));
            AddEntry(archive, EntryDll, [1, 2, 3]);
            AddEntry(archive, "../escaped.txt", [1]);
        }

        PluginCheckReport report = await PluginCheckRunner.RunAsync(zipPath);

        Assert.Equal(PluginCheckVerdict.WouldBeRefused, report.Verdict);
        PluginCheckItem check = Assert.Single(report.Checks, c => c.Check == "zip extracts safely");
        Assert.Equal(PluginCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task AMissingPathThrowsUsage()
    {
        string missing = Path.Combine(_root, "does-not-exist");

        await Assert.ThrowsAsync<PluginCheckUsageException>(() => PluginCheckRunner.RunAsync(missing));
    }

    [Fact]
    public async Task ANonZipFileThrowsUsage()
    {
        Directory.CreateDirectory(_root);
        string textFile = Path.Combine(_root, "notes.txt");
        File.WriteAllText(textFile, "not a plugin");

        await Assert.ThrowsAsync<PluginCheckUsageException>(() => PluginCheckRunner.RunAsync(textFile));
    }

    [Fact]
    public async Task EntryPointExitCodesMatchTheVerdict()
    {
        string validDirectory = WriteValidPluginFolder("exit-code-valid");
        string refusedDirectory = WriteValidPluginFolder(
            "exit-code-refused", ValidManifestJson(id: "Bad.Id"));
        string missing = Path.Combine(_root, "exit-code-missing");

        Assert.Equal(0, await RunEntryPointAsync(validDirectory));
        Assert.Equal(1, await RunEntryPointAsync(refusedDirectory));
        Assert.Equal(2, await RunEntryPointAsync(missing));
        Assert.Equal(2, await RunEntryPointAsync());
    }

    [Fact]
    public async Task JsonModePutsOnlyJsonOnStdout()
    {
        string directory = WriteValidPluginFolder("json-output");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exitCode = await PluginCheckEntryPoint.RunAsync(
            [directory, "--json"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr.ToString());
        string output = stdout.ToString();
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("directory", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal("would-install", document.RootElement.GetProperty("verdict").GetString());
        Assert.True(document.RootElement.GetProperty("checks").GetArrayLength() > 0);
        // The document must be the only thing written: parsing all of stdout as one JSON value
        // already proves nothing else shares the stream, and the trailing newline this tool adds
        // is the sole character left over.
        Assert.Equal('\n', output[^1]);
    }

    private static async Task<int> RunEntryPointAsync(params string[] arguments) =>
        await PluginCheckEntryPoint.RunAsync(arguments, TextWriter.Null, TextWriter.Null);

    private static void AddEntry(ZipArchive archive, string name, byte[] content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        stream.Write(content);
    }

    private string WriteValidPluginFolder(string folderName, string? manifestJson = null)
    {
        string directory = Path.Combine(_root, folderName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifestJson ?? ValidManifestJson());
        File.WriteAllBytes(Path.Combine(directory, EntryDll), [1, 2, 3]);
        return directory;
    }

    private string WriteValidPluginZip(string fileBaseName, string manifestJson, byte[]? icon = null)
    {
        Directory.CreateDirectory(_root);
        string zipPath = Path.Combine(_root, fileBaseName + ".zip");
        using FileStream stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        AddEntry(archive, "plugin.json", Encoding.UTF8.GetBytes(manifestJson));
        AddEntry(archive, EntryDll, [1, 2, 3]);
        if (icon is not null)
        {
            AddEntry(archive, "icon.png", icon);
        }

        return zipPath;
    }

    private static string ValidManifestJson(string? id = null, string? version = null) => $$"""
        {
          "id": "{{id ?? Id}}",
          "displayName": "Hello",
          "version": "{{version ?? "1.0.0"}}",
          "entryDll": "{{EntryDll}}",
          "apiVersion": 1,
          "minHostVersion": "0.1.0",
          "hosts": ["headless"]
        }
        """;
}
