using System.Linq;
using System.Reflection;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Runtime.Chat;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The chat entry -- the draft, where a submitted line goes, the channel it
/// goes on and what was sent before -- has a single owner the whole client
/// shares, so that every front end that can be typed into is a view of the
/// same entry rather than the owner of its own.
///
/// A chat window, or a set of chat bindings, that can be constructed without
/// one would quietly get a private entry instead: the draft would be invisible
/// to everything else, a submitted line would go out on the wrong channel, and
/// anything asking whether the player is typing would be told about the wrong
/// entry. Nothing in a running client would report the split; it would look
/// like a chat box that simply forgot things.
///
/// So the entry is not an option a caller may leave out. These tests fail the
/// day either construction path makes it one again.
/// </summary>
public sealed class ChatEntryOwnerIsRequiredTests
{
    [Fact]
    public void BindingAChatWindowRequiresTheSharedChatEntry()
    {
        ParameterInfo entry = typeof(ChatWindowController)
            .GetMethod(
                nameof(ChatWindowController.Bind),
                BindingFlags.Public | BindingFlags.Static)!
            .GetParameters()
            .Single(parameter => parameter.Name == "entry");

        Assert.Equal(typeof(RuntimeChatEntryOwner), entry.ParameterType);
        Assert.False(
            entry.IsOptional,
            "a chat window must be given the shared chat entry, not fall back "
            + "to one of its own");
    }

    [Fact]
    public void TheChatBindingsRequireTheSharedChatEntry()
    {
        ParameterInfo entry = typeof(ChatRuntimeBindings)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == "Entry");

        Assert.Equal(typeof(RuntimeChatEntryOwner), entry.ParameterType);
        Assert.False(
            entry.IsOptional,
            "the chat bindings must carry the shared chat entry, not leave it "
            + "to whatever they are bound to");
    }
}
