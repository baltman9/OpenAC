using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.ViewModels;

namespace AcDream.Launcher.Tests;

public sealed class PluginInstallDialogViewModelTests
{
    private static readonly PluginCharacterOption[] Characters =
    [
        new("Local ACE", "acct-a", "+First", "+First (acct-a@Local ACE)"),
        new("Local ACE", "acct-b", "+Second", "+Second (acct-b@Local ACE)"),
    ];

    [Fact]
    public async Task DefaultChoiceIsNoneAndConfirmWritesNoCharacterList()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        Assert.True(dialog.EnableNone);
        Assert.False(dialog.EnableAll);
        Assert.False(dialog.EnableChoose);

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Empty(enableCalls);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task ChoosingAllEnablesEveryOfferedCharacter()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.EnableAll = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        (string Id, IReadOnlyList<PluginCharacterOption> Chosen) call = Assert.Single(enableCalls);
        Assert.Equal("edwards.hello", call.Id);
        Assert.Equal(Characters, call.Chosen);
    }

    [Fact]
    public async Task ChoosingSpecificCharactersEnablesOnlyThoseChecked()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.EnableChoose = true;
        dialog.Characters[1].IsChecked = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        (string Id, IReadOnlyList<PluginCharacterOption> Chosen) call = Assert.Single(enableCalls);
        Assert.Equal(Characters[1], Assert.Single(call.Chosen));
    }

    [Fact]
    public async Task ChoosingWithNothingCheckedWritesNoCharacterList()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.EnableChoose = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Empty(enableCalls);
    }

    [Fact]
    public async Task EnableFailureAfterInstallStillClosesTheDialogAndNeverReinstalls()
    {
        var dialog = new PluginInstallDialogViewModel();
        int installCount = 0;
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) =>
            {
                installCount++;
                return Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false));
            },
            (_, _) => throw new InvalidOperationException("could not save the character profile"));

        dialog.EnableAll = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.False(dialog.IsOpen);
        Assert.Equal(1, installCount);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, installCount);
    }

    [Fact]
    public void ConfirmIsEnabledAssoonAsAListedInstallOpensWithNoTick()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { });

        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.",
            dialog.WarningText);
    }

    [Fact]
    public void ConfirmIsEnabledAssoonAsAnUnlistedInstallOpensWithNoTick()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "someone-else/some-plugin",
            "someone.plugin",
            "Some Plugin",
            isListed: false,
            isUpdate: false,
            null,
            [],
            (_, _) => Task.FromResult(new PluginInstallResult("someone.plugin", "0.1.0", WasUpdate: false)),
            (_, _) => { });

        Assert.True(dialog.ConfirmCommand.CanExecute(null));
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.\n"
            + "This plugin is not on the OpenAC plugin list.",
            dialog.WarningText);
    }

    [Fact]
    public void APrereleaseOfferedVersionAddsAPreReleaseSentenceToTheNotice()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            "0.2.0-beta.1",
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0-beta.1", WasUpdate: false)),
            (_, _) => { });

        Assert.True(dialog.IsOfferedPrerelease);
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.\n"
            + "This version is a pre-release.",
            dialog.WarningText);
    }

    [Fact]
    public void AStableOfferedVersionAddsNoPreReleaseSentence()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            "0.2.0",
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: false)),
            (_, _) => { });

        Assert.False(dialog.IsOfferedPrerelease);
        Assert.Equal(
            "Plugins are made by third parties, not OpenAC. Installing one is your choice and "
            + "your responsibility. Only install plugins from authors you trust.",
            dialog.WarningText);
    }

    [Fact]
    public void UpdateOpensWithNoEnableChoiceOffered()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { });

        Assert.True(dialog.IsUpdate);
        Assert.False(dialog.ShowEnableChoice);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConfirmingAnUpdateWritesNoCharacterList()
    {
        var dialog = new PluginInstallDialogViewModel();
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (id, chosen) => enableCalls.Add((id, chosen)));

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.False(dialog.IsOpen);
        Assert.Empty(enableCalls);
    }

    [Fact]
    public async Task CancelClosesWithoutInstallingOrEnabling()
    {
        var dialog = new PluginInstallDialogViewModel();
        bool installCalled = false;
        List<(string Id, IReadOnlyList<PluginCharacterOption> Characters)> enableCalls = [];
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) =>
            {
                installCalled = true;
                return Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false));
            },
            (id, chosen) => enableCalls.Add((id, chosen)));

        dialog.CancelCommand.Execute(null);

        Assert.False(dialog.IsOpen);
        Assert.False(installCalled);
        Assert.Empty(enableCalls);
        await Task.CompletedTask;
    }

    [Fact]
    public void DeclaringNoCapabilitiesShowsNoChips()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { });

        Assert.False(dialog.HasCapabilities);
        Assert.Empty(dialog.Capabilities);
    }

    [Fact]
    public void EveryDeclaredCapabilityReachesTheDialogWithItsNoteIntact()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { },
            [
                new LauncherPluginCapabilityDeclaration(
                    LauncherPluginCapability.Network, "Sends buff usage counts to my server."),
                new LauncherPluginCapabilityDeclaration(
                    LauncherPluginCapability.Chat, "Reads chat to detect buff requests."),
            ]);

        Assert.True(dialog.HasCapabilities);
        Assert.Equal(2, dialog.Capabilities.Count);
        Assert.Equal("Sends buff usage counts to my server.", dialog.Capabilities[0].Note);
        Assert.Equal("Reads chat to detect buff requests.", dialog.Capabilities[1].Note);
        Assert.Equal("The author says this plugin:", dialog.CapabilitiesHeading);
    }

    [Fact]
    public void CapabilityDisplayOrderFollowsTheVocabularyNotTheManifestArray()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { },
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Reaches out."),
            ]);

        Assert.Equal("uses network", dialog.Capabilities[0].Label);
        Assert.Equal("uses chat", dialog.Capabilities[1].Label);
    }

    [Fact]
    public void UnknownCapabilitiesShowLoadingAndDisableInstallUntilTheFetchCompletes()
    {
        var dialog = new PluginInstallDialogViewModel();
        var fetch = new TaskCompletionSource<IReadOnlyList<LauncherPluginCapabilityDeclaration>>();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { },
            capabilities: null,
            loadCapabilities: _ => fetch.Task);

        Assert.True(dialog.IsLoadingCapabilities);
        Assert.False(dialog.HasCapabilities);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        fetch.SetResult(
        [
            new LauncherPluginCapabilityDeclaration(
                LauncherPluginCapability.Network, "Sends buff usage counts to my server."),
            new LauncherPluginCapabilityDeclaration(
                LauncherPluginCapability.Chat, "Reads chat to detect buff requests."),
        ]);

        Assert.False(dialog.IsLoadingCapabilities);
        Assert.True(dialog.HasCapabilities);
        Assert.Equal(2, dialog.Capabilities.Count);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public void UnknownCapabilitiesLeaveInstallDisabledWithAReasonWhenTheFetchFails()
    {
        var dialog = new PluginInstallDialogViewModel();
        var fetch = new TaskCompletionSource<IReadOnlyList<LauncherPluginCapabilityDeclaration>>();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { },
            capabilities: null,
            loadCapabilities: _ => fetch.Task);

        fetch.SetException(new InvalidOperationException("GitHub is rate limiting; try later."));

        Assert.False(dialog.IsLoadingCapabilities);
        Assert.True(dialog.HasCapabilitiesLoadError);
        Assert.Equal("GitHub is rate limiting; try later.", dialog.CapabilitiesLoadError);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        Assert.True(dialog.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void KnownCapabilitiesNeverShowLoadingEvenWhenEmpty()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false)),
            (_, _) => { },
            capabilities: []);

        Assert.False(dialog.IsLoadingCapabilities);
        Assert.False(dialog.HasCapabilitiesLoadError);
        Assert.True(dialog.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConfirmPassesTheDisplayedCapabilitiesFetchedOnDemandToInstall()
    {
        var dialog = new PluginInstallDialogViewModel();
        var fetch = new TaskCompletionSource<IReadOnlyList<LauncherPluginCapabilityDeclaration>>();
        IReadOnlyList<LauncherPluginCapabilityDeclaration>? passedToInstall = null;
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: false,
            null,
            Characters,
            (capabilities, _) =>
            {
                passedToInstall = capabilities;
                return Task.FromResult(new PluginInstallResult("edwards.hello", "0.1.0", WasUpdate: false));
            },
            (_, _) => { },
            capabilities: null,
            loadCapabilities: _ => fetch.Task);

        var declared = new LauncherPluginCapabilityDeclaration(
            LauncherPluginCapability.Network, "Sends buff usage counts to my server.");
        fetch.SetResult([declared]);

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Equal([declared], passedToInstall);
    }

    [Fact]
    public void UpdateWithIdenticalReorderedCapabilitiesNeedsNoFreshConsent()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
            ],
            installedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
            ]);

        Assert.False(dialog.CapabilitiesChanged);
        Assert.False(dialog.ShowKeepEnabledChoice);
        Assert.Equal("Install", dialog.ConfirmLabel);
        Assert.All(dialog.Capabilities, chip => Assert.False(chip.IsNew));
    }

    [Fact]
    public void UpdateAddingACapabilityMarksItNewAndUsesTheConsentLabel()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
            ],
            installedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
            ]);

        Assert.True(dialog.CapabilitiesChanged);
        Assert.Equal("Update and allow", dialog.ConfirmLabel);
        Assert.False(dialog.Capabilities[0].IsNew);
        Assert.True(dialog.Capabilities[1].IsNew);
        Assert.Equal("uses chat (new)", dialog.Capabilities[1].Label);
    }

    [Fact]
    public void UpdateWithARewordedNoteCountsAsChangedButNotAsNew()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities:
            [
                new LauncherPluginCapabilityDeclaration(
                    LauncherPluginCapability.Network, "Sends usage counts every hour."),
            ],
            installedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
            ]);

        Assert.True(dialog.CapabilitiesChanged);
        Assert.Equal("Update and allow", dialog.ConfirmLabel);
        Assert.False(Assert.Single(dialog.Capabilities).IsNew);
    }

    [Fact]
    public void AffectedCharactersTextNamesEveryoneCurrentlyEnabled()
    {
        var dialog = new PluginInstallDialogViewModel();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities: [new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat.")],
            installedCapabilities: [],
            affectedCharacters: ["+First (acct-a@Local ACE)", "+Second (acct-b@Local ACE)"]);

        Assert.Equal(
            "Currently enabled for: +First (acct-a@Local ACE), +Second (acct-b@Local ACE).",
            dialog.AffectedCharactersText);
    }

    [Fact]
    public async Task ConfirmingAnUpdateWithChangedCapabilitiesStripsEveryAffectedCharacterByDefault()
    {
        var dialog = new PluginInstallDialogViewModel();
        var disabledIds = new List<string>();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
            ],
            installedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
            ],
            affectedCharacters: ["+First (acct-a@Local ACE)"],
            disableForAllCharacters: disabledIds.Add);

        Assert.True(dialog.ShowKeepEnabledChoice);
        Assert.False(dialog.KeepEnabled);

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Equal(["edwards.hello"], disabledIds);
    }

    [Fact]
    public async Task ConfirmingWithKeepEnabledLeavesEveryAffectedCharacterAlone()
    {
        var dialog = new PluginInstallDialogViewModel();
        var disabledIds = new List<string>();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
            ],
            installedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
            ],
            affectedCharacters: ["+First (acct-a@Local ACE)"],
            disableForAllCharacters: disabledIds.Add);

        dialog.KeepEnabled = true;
        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Empty(disabledIds);
    }

    [Fact]
    public async Task ChangedCapabilitiesWithNoAffectedCharactersNeverCallsDisable()
    {
        var dialog = new PluginInstallDialogViewModel();
        var disabledIds = new List<string>();
        dialog.Open(
            "shaneedwards/openac-plugin-hello",
            "edwards.hello",
            "Hello",
            isListed: true,
            isUpdate: true,
            null,
            Characters,
            (_, _) => Task.FromResult(new PluginInstallResult("edwards.hello", "0.2.0", WasUpdate: true)),
            (_, _) => { },
            capabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Chat, "Reads chat."),
            ],
            installedCapabilities:
            [
                new LauncherPluginCapabilityDeclaration(LauncherPluginCapability.Network, "Sends usage counts."),
            ],
            affectedCharacters: [],
            disableForAllCharacters: disabledIds.Add);

        Assert.False(dialog.ShowKeepEnabledChoice);

        await dialog.ConfirmCommand.ExecuteAsync();

        Assert.Empty(disabledIds);
    }
}
