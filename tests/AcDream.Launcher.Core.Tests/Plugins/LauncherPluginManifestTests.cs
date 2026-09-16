using AcDream.Launcher.Core.Plugins;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class LauncherPluginManifestTests
{
    private const string ValidJson = """
        {
          "id": "edwards.hello",
          "displayName": "Hello",
          "version": "0.1.0",
          "entryDll": "Hello.dll",
          "apiVersion": 1,
          "minHostVersion": "0.1.7",
          "hosts": ["graphical", "headless"]
        }
        """;

    [Fact]
    public void ParsesAllFields()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(ValidJson);

        Assert.Equal("edwards.hello", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal("Hello.dll", manifest.EntryDll);
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Equal(new LauncherPluginHostVersion(0, 1, 7), manifest.MinHostVersion);
        Assert.Equal(
            [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless],
            manifest.Hosts);
        manifest.ValidateForInstall();
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "somethingNew": "value"
            }
            """);

        Assert.Equal("edwards.hello", manifest.Id);
    }

    [Fact]
    public void ValidateForInstallRejectsAnIdWithATrailingNewline()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"edwards.hello\"", "\"edwards.hello\\n\""));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Edwards.Hello")]
    [InlineData("edwards.")]
    [InlineData(".hello")]
    [InlineData("edwards..hello")]
    public void ValidateForInstallRejectsABadId(string id)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"edwards.hello\"", $"\"{id}\""));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0")]
    public void ValidateForInstallRejectsANonSemVerVersion(string version)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"0.1.0\"", $"\"{version}\""));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRequiresMinHostVersion()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "hosts": ["graphical"]
            }
            """);

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRequiresHosts()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.7"
            }
            """);

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRejectsAnUnsupportedApiVersion()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"apiVersion\": 1", "\"apiVersion\": 2"));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void RejectsMinHostVersionAboveMaxHostVersion()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.2.0",
              "maxHostVersion": "0.1.0"
            }
            """));
    }

    [Theory]
    [InlineData("v0.1.0", true)]
    [InlineData("v0.1.1", false)]
    [InlineData("0.1.0", false)]
    public void MatchesTagRequiresAVLeadingVersion(string tag, bool expected)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(ValidJson);

        Assert.Equal(expected, manifest.MatchesTag(tag));
    }

    [Fact]
    public void AbsentCapabilitiesParseToEmpty()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(ValidJson);

        Assert.Equal(0, manifest.CapabilitiesVersion);
        Assert.Empty(manifest.Capabilities);
        Assert.Empty(manifest.UnrecognizedCapabilities);
        manifest.ValidateForInstall();
    }

    [Fact]
    public void ValidCapabilitiesRoundTripWithNotesIntact()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.7",
              "hosts": ["graphical"],
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "Sends buff usage counts to my server." },
                { "name": "chat", "note": "Reads chat to detect buff requests." }
              ]
            }
            """);

        Assert.Equal(1, manifest.CapabilitiesVersion);
        Assert.Equal(
            [
                new LauncherPluginCapabilityDeclaration(
                    LauncherPluginCapability.Network, "Sends buff usage counts to my server."),
                new LauncherPluginCapabilityDeclaration(
                    LauncherPluginCapability.Chat, "Reads chat to detect buff requests."),
            ],
            manifest.Capabilities);
        Assert.Empty(manifest.UnrecognizedCapabilities);
        manifest.ValidateForInstall();
    }

    [Fact]
    public void ParseKeepsAnUnrecognizedCapabilityButValidateForInstallRefusesIt()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.7",
              "hosts": ["graphical"],
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "teleportsPlayers", "note": "Moves the player around instantly." }
              ]
            }
            """);

        Assert.Equal(["teleportsPlayers"], manifest.UnrecognizedCapabilities);
        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRefusesACapabilitiesVersionAboveCurrentBeforeCheckingUnrecognizedNames()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.7",
              "hosts": ["graphical"],
              "capabilitiesVersion": 99,
              "capabilities": [
                { "name": "notARealCapability", "note": "Something new." }
              ]
            }
            """);

        LauncherPluginCapabilityVersionException ex = Assert.Throws<LauncherPluginCapabilityVersionException>(
            manifest.ValidateForInstall);

        Assert.Equal(99, ex.DeclaredVersion);
        Assert.Equal(LauncherPluginCapabilityVocabulary.Current, ex.CurrentVersion);
    }

    [Fact]
    public void ACapabilitiesVersionAboveCurrentRefusesOnTheVersionRatherThanTheEntryRules()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.7",
              "hosts": ["graphical"],
              "capabilitiesVersion": 99,
              "capabilities": [
                { "name": "network", "note": "https://example.invalid/why" }
              ]
            }
            """);

        Assert.Empty(manifest.Capabilities);
        Assert.Empty(manifest.UnrecognizedCapabilities);
        Assert.Throws<LauncherPluginCapabilityVersionException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void RejectsADuplicateCapabilityName()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "Sends usage counts." },
                { "name": "Network", "note": "Also reaches the network." }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsABlankNote()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "   " }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsANoteOver120CharactersAfterTrimming()
    {
        string note = new string('a', 121);
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse($$"""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "{{note}}" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsANoteWithANewline()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "Sends data.\nAlso this." }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsANoteWithABidirectionalOverride()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "Sends data ‮gnihton.tsod." }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsANoteContainingALink()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilitiesVersion": 1,
              "capabilities": [
                { "name": "network", "note": "See https://example.com for details." }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsCapabilitiesWithNoCapabilitiesVersion()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "capabilities": [
                { "name": "network", "note": "Sends usage counts." }
              ]
            }
            """));
    }
}
