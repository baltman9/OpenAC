using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Profiles;

namespace AcDream.Launcher.Core.Tests.Orchestration;

/// <summary>A launch to the character screen fixes the plugin list before a character is picked,
/// so it may carry only what every character on the account has enabled.</summary>
public sealed class CharacterScreenPluginListTests
{
    private static AccountProfile Account(params string[][] pluginsPerCharacter) => new()
    {
        Account = "testaccount",
        Password = "pw",
        Characters = [.. pluginsPerCharacter.Select((plugins, index) => new CharacterProfile
        {
            Name = $"+Char{index}",
            Plugins = [.. plugins],
        })],
    };

    [Fact]
    public void APluginEveryCharacterEnabledIsLoaded()
    {
        AccountProfile account = Account(["a.one", "b.two"], ["B.Two", "a.one", "c.three"]);

        Assert.Equal(
            ["a.one", "b.two"],
            LauncherOrchestrator.PluginsEnabledForEveryCharacter(account));
    }

    [Fact]
    public void APluginOneCharacterLeftOutIsNotLoaded()
    {
        AccountProfile account = Account(["a.one"], []);

        Assert.Empty(LauncherOrchestrator.PluginsEnabledForEveryCharacter(account));
    }

    [Fact]
    public void AnAccountWithNoKnownCharactersLoadsNone()
    {
        Assert.Empty(LauncherOrchestrator.PluginsEnabledForEveryCharacter(Account()));
    }

    [Fact]
    public void TheLoadNoneMarkerIsNeverPassedOnAsAPlugin()
    {
        AccountProfile account = Account(["none"], ["none"]);

        Assert.Empty(LauncherOrchestrator.PluginsEnabledForEveryCharacter(account));
    }
}
