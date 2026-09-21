using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The windowed client reads a character's own saved preferences when that
/// character arrives in the world; the windowless client reads nothing. That
/// difference is listed as an exception rather than work outstanding, and the
/// reason it is allowed to be one is narrow: the loaded record has no reader.
/// Nothing in the runtime, nothing a plugin can reach and nothing in the
/// client's behaviour asks it what the default chat channel is, whether to
/// auto-attack, whether to confirm a salvage or whether to print pickup
/// messages. The only other thing the same step does is put the journal panel
/// back, and there is no panel tree without a window.
///
/// That is a fact about today's code, not a rule, so it is pinned here. The
/// day something reads the record the exception stops being true and these
/// tests fail, which is the signal to move the preferences into the runtime
/// and make both clients load them.
/// </summary>
public sealed class CharacterSettingsHaveNoRuntimeReaderTests
{
    /// <summary>
    /// The files allowed to name the preferences: the record itself and the
    /// store that reads and writes it.
    /// </summary>
    private static readonly string[] DeclaringFiles =
    [
        Path.Combine(
            "src", "AcDream.UI.Abstractions", "Panels", "Settings",
            "CharacterSettings.cs"),
        Path.Combine(
            "src", "AcDream.UI.Abstractions", "Panels", "Settings",
            "SettingsStore.cs"),
    ];

    /// <summary>
    /// The one place allowed to hold a loaded record without reading a field
    /// out of it: the windowed client's settings owner, which loads it.
    /// </summary>
    private static readonly string SettingsOwnerFile = Path.Combine(
        "src", "AcDream.App", "Settings", "RuntimeSettingsController.cs");

    [Theory]
    [InlineData("DefaultChatChannel")]
    [InlineData("AutoAttack")]
    [InlineData("ConfirmSalvage")]
    [InlineData("ShowPickupMessages")]
    public void NothingOutsideTheStoreAsksAPreferenceWhatItSays(string member)
    {
        var pattern = new Regex($@"\b{member}\b", RegexOptions.CultureInvariant);
        List<string> readers = [];

        foreach (string file in SourceFiles())
        {
            string relative = Path.GetRelativePath(RepositoryRoot(), file);
            if (DeclaringFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;

            if (pattern.IsMatch(File.ReadAllText(file)))
                readers.Add(relative);
        }

        Assert.True(
            readers.Count == 0,
            $"'{member}' is read in {string.Join(", ", readers)}. A character's "
            + "saved preferences now decide something, so the windowless "
            + "client reading none of them is a real difference again: move "
            + "them into the runtime and delete the exception that says "
            + "nothing depends on them.");
    }

    [Fact]
    public void OnlyTheSettingsOwnerEvenHoldsALoadedRecord()
    {
        // The record used as a type: declared, constructed, or its default
        // taken. Names that merely end in the same word -- a keyboard action,
        // a launcher call -- are not it.
        var pattern = new Regex(
            @"\bCharacterSettings(\s+[A-Za-z_]|\.Default|\s*\()",
            RegexOptions.CultureInvariant);
        List<string> holders = [];

        foreach (string file in SourceFiles())
        {
            string relative = Path.GetRelativePath(RepositoryRoot(), file);
            if (DeclaringFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)
                || string.Equals(
                    relative,
                    SettingsOwnerFile,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pattern.IsMatch(File.ReadAllText(file)))
                holders.Add(relative);
        }

        Assert.Empty(holders);
    }

    /// <summary>
    /// The same fact from the other side: nothing a plugin is handed carries
    /// the record, so no plugin can be reading it whatever the source says.
    /// </summary>
    [Fact]
    public void NoRuntimeOrPluginFacingTypeCarriesTheRecord()
    {
        Assembly[] assemblies =
        [
            typeof(AcDream.Runtime.GameRuntime).Assembly,
            typeof(AcDream.Plugin.Abstractions.IPluginHost).Assembly,
        ];

        List<string> carriers = [];
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                carriers.AddRange(
                    type.GetProperties(
                            BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance | BindingFlags.Static)
                        .Where(static property =>
                            property.PropertyType == typeof(CharacterSettings))
                        .Select(property => $"{type.FullName}.{property.Name}"));
                carriers.AddRange(
                    type.GetFields(
                            BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance | BindingFlags.Static)
                        .Where(static field =>
                            field.FieldType == typeof(CharacterSettings))
                        .Select(field => $"{type.FullName}.{field.Name}"));
            }
        }

        Assert.Empty(carriers);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        string[] starts =
        [
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        ];
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
                continue;

            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the working or output directory.");
    }
}
