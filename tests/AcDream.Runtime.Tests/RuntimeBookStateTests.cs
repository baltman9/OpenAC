using System;
using System.Collections.Generic;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;
using Xunit;

namespace AcDream.Runtime.Tests;

public sealed class RuntimeBookStateTests
{
    private const uint Player = 0x5000000Au;
    private const uint Other = 0x5000000Bu;
    private const uint BookGuid = 0x80001234u;

    private static BookPage Page(
        uint author, string text, uint textIncluded = 1u, uint ignoreAuthor = 0u) =>
        new(author, "Someone", "acct", textIncluded, ignoreAuthor, text);

    private static BookEvents.OpenBook Open(
        params BookPage[] pages) =>
        Open(BookGuid, 8, 500, pages);

    private static BookEvents.OpenBook Open(
        uint guid, int maxPages, int maxChars, params BookPage[] pages) =>
        new(
            guid,
            maxPages,
            new BookPageList(maxPages, maxChars, pages),
            "An Inscription",
            Other,
            "Scribbler");

    private static RuntimeBookState NewState(uint playerGuid = Player) =>
        new(() => playerGuid);

    [Fact]
    public void ApplyOpenBook_StoresTheBookAndOpensTheFirstPage()
    {
        RuntimeBookState state = NewState();

        state.ApplyOpenBook(Open(Page(Player, "one"), Page(Player, "two")));

        RuntimeBookSnapshot snapshot = state.Snapshot;
        Assert.True(snapshot.IsOpen);
        Assert.Equal(BookGuid, snapshot.BookGuid);
        Assert.Equal(8, snapshot.MaxNumPages);
        Assert.Equal(500, snapshot.MaxNumCharsPerPage);
        Assert.Equal(2, snapshot.PageCount);
        Assert.Equal(0, snapshot.CurrentPage);
        Assert.Equal("An Inscription", snapshot.Inscription);
        Assert.Equal(Other, snapshot.ScribeId);
        Assert.Equal("Scribbler", snapshot.ScribeName);
        Assert.False(snapshot.RequestPending);
    }

    [Fact]
    public void ApplyOpenBook_OfTheSameBookKeepsThePageTheReaderWasOn()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(
            Open(Page(Player, "one"), Page(Player, "two"), Page(Player, "three")));
        Assert.Equal(
            RuntimeBookPageAction.Display, state.SetCurrentPage(2));

        state.ApplyOpenBook(
            Open(Page(Player, "one"), Page(Player, "two"), Page(Player, "three")));

        Assert.Equal(2, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ApplyOpenBook_FallsBackToTheLastPageThatStillExists()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(
            Open(Page(Player, "one"), Page(Player, "two"), Page(Player, "three")));
        state.SetCurrentPage(2);

        state.ApplyOpenBook(Open(Page(Player, "one")));

        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ApplyOpenBook_OfAnEmptyBookOpensPageZero()
    {
        RuntimeBookState state = NewState();

        state.ApplyOpenBook(Open());

        Assert.Equal(0, state.Snapshot.CurrentPage);
        Assert.Equal(0, state.Snapshot.PageCount);
    }

    [Fact]
    public void ApplyOpenBook_OfADifferentBookDoesNotCarryThePage()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(
            Open(Page(Player, "one"), Page(Player, "two")));
        state.SetCurrentPage(1);

        state.ApplyOpenBook(
            Open(0x80009999u, 8, 500, Page(Player, "a"), Page(Player, "b")));

        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void SetCurrentPage_ToAPageWithTextJustDisplaysIt()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one"), Page(Player, "two")));

        Assert.Equal(RuntimeBookPageAction.Display, state.SetCurrentPage(1));
        Assert.False(state.Snapshot.RequestPending);
        Assert.Equal(1, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void SetCurrentPage_ToAPageWithoutTextAsksForItAndGatesFurtherTurns()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(
            Open(Page(Player, "one"), Page(Player, string.Empty, textIncluded: 0u)));

        Assert.Equal(RuntimeBookPageAction.RequestPageText, state.SetCurrentPage(1));
        Assert.True(state.Snapshot.RequestPending);

        // A second turn while the first is in flight is refused.
        Assert.Equal(RuntimeBookPageAction.None, state.SetCurrentPage(0));
        Assert.Equal(1, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void SetCurrentPage_OnePastTheEndAsksForANewPage()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));

        Assert.Equal(RuntimeBookPageAction.AddPage, state.SetCurrentPage(1));
        Assert.True(state.Snapshot.RequestPending);
    }

    [Fact]
    public void SetCurrentPage_MoreThanOnePastTheEndIsRefused()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));

        Assert.Equal(RuntimeBookPageAction.None, state.SetCurrentPage(2));
        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void SetCurrentPage_BeyondThePageCapIsRefused()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(BookGuid, 1, 500, Page(Player, "one")));

        Assert.Equal(RuntimeBookPageAction.None, state.SetCurrentPage(1));
    }

    [Fact]
    public void SetCurrentPage_NegativeOrUnchangedIsRefused()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));

        Assert.Equal(RuntimeBookPageAction.None, state.SetCurrentPage(-1));
        Assert.Equal(RuntimeBookPageAction.None, state.SetCurrentPage(0));
    }

    [Fact]
    public void SetCurrentPage_WithNoBookOpenIsRefused()
    {
        Assert.Equal(RuntimeBookPageAction.None, NewState().SetCurrentPage(0));
    }

    [Fact]
    public void ApplyPageData_FillsThePageAndLiftsTheGate()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(
            Open(Page(Player, "one"), Page(Player, string.Empty, textIncluded: 0u)));
        state.SetCurrentPage(1);

        bool handled = state.ApplyPageData(
            new BookEvents.PageDataResponse(BookGuid, 1, Page(Other, "fetched")));

        Assert.True(handled);
        Assert.False(state.Snapshot.RequestPending);
        Assert.Equal("fetched", state.View.GetPage(1)!.Value.PageText);
    }

    [Fact]
    public void ApplyPageData_ForAnotherBookIsDropped()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));

        bool handled = state.ApplyPageData(
            new BookEvents.PageDataResponse(0x80009999u, 0, Page(Other, "wrong")));

        Assert.False(handled);
        Assert.Equal("one", state.View.GetPage(0)!.Value.PageText);
    }

    [Fact]
    public void ApplyAddPageResponse_ForTheOpenPageAddsABlankLocalPage()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));
        Assert.Equal(RuntimeBookPageAction.AddPage, state.SetCurrentPage(1));

        bool folded = state.ApplyAddPageResponse(
            new BookEvents.PageResponse(BookGuid, 1, true), "Acdream");

        Assert.True(folded);
        Assert.False(state.Snapshot.RequestPending);
        Assert.Equal(2, state.Snapshot.PageCount);
        BookPage added = state.View.GetPage(1)!.Value;
        Assert.Equal(Player, added.AuthorId);
        Assert.Equal("Acdream", added.AuthorName);
        Assert.Equal(string.Empty, added.PageText);
        Assert.True(state.View.IsPageEditable(1));
    }

    [Fact]
    public void ApplyAddPageResponse_ThatFailsAsksForAFullReRead()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));
        state.SetCurrentPage(1);

        bool folded = state.ApplyAddPageResponse(
            new BookEvents.PageResponse(BookGuid, 1, false), "Acdream");

        Assert.False(folded);
    }

    [Fact]
    public void ApplyAddPageResponse_ForAnotherPageMovesThereAndAsksForAReRead()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));
        state.SetCurrentPage(1);

        bool folded = state.ApplyAddPageResponse(
            new BookEvents.PageResponse(BookGuid, 5, true), "Acdream");

        Assert.False(folded);
        Assert.Equal(5, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ApplyDeletePageResponse_RemovesThePageAndPullsTheReaderBack()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one"), Page(Player, "two")));
        state.SetCurrentPage(1);

        state.ApplyDeletePageResponse(new BookEvents.PageResponse(BookGuid, 1, true));

        Assert.Equal(1, state.Snapshot.PageCount);
        Assert.Equal(0, state.Snapshot.CurrentPage);
        Assert.False(state.Snapshot.RequestPending);
    }

    [Fact]
    public void ApplyModifyPageResponse_OnlyLiftsTheGate()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));
        state.MarkRequestPending();

        state.ApplyModifyPageResponse(new BookEvents.PageResponse(BookGuid, 0, true));

        Assert.False(state.Snapshot.RequestPending);
        Assert.Equal("one", state.View.GetPage(0)!.Value.PageText);
    }

    [Fact]
    public void IsPageEditable_TracksAuthorshipAndTheIgnoreAuthorFlag()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(
            Page(Player, "mine"),
            Page(Other, "theirs"),
            Page(Other, "communal", ignoreAuthor: 1u)));

        Assert.True(state.View.IsPageEditable(0));
        Assert.False(state.View.IsPageEditable(1));
        Assert.True(state.View.IsPageEditable(2));
        Assert.False(state.View.IsPageEditable(9));
    }

    [Fact]
    public void FlushCurrentPage_SavesEditedTextOnAPageTheReaderMayWrite()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "old")));

        RuntimeBookFlushAction action = state.FlushCurrentPage("new", out int page);

        Assert.Equal(RuntimeBookFlushAction.ModifyPage, action);
        Assert.Equal(0, page);
        Assert.Equal("new", state.View.GetPage(0)!.Value.PageText);
    }

    [Fact]
    public void FlushCurrentPage_OnSomeoneElsesPageSendsNothing()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Other, "theirs")));

        Assert.Equal(
            RuntimeBookFlushAction.None, state.FlushCurrentPage("edit", out _));
        Assert.Equal("theirs", state.View.GetPage(0)!.Value.PageText);
    }

    [Fact]
    public void FlushCurrentPage_OnACommunalPageSavesEvenThoughAnotherWroteIt()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Other, "theirs", ignoreAuthor: 1u)));

        Assert.Equal(
            RuntimeBookFlushAction.ModifyPage,
            state.FlushCurrentPage("edit", out _));
    }

    [Fact]
    public void FlushCurrentPage_BlankedByItsOwnAuthorDeletesThePage()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one"), Page(Player, "two")));
        state.SetCurrentPage(1);

        RuntimeBookFlushAction action = state.FlushCurrentPage("  \n ", out int page);

        Assert.Equal(RuntimeBookFlushAction.DeletePage, action);
        Assert.Equal(1, page);
        Assert.Equal(1, state.Snapshot.PageCount);
    }

    [Fact]
    public void FlushCurrentPage_BlankedOnACommunalPageWrittenByAnotherIsSavedNotDeleted()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Other, "theirs", ignoreAuthor: 1u)));

        Assert.Equal(
            RuntimeBookFlushAction.ModifyPage, state.FlushCurrentPage(" ", out _));
        Assert.Equal(1, state.Snapshot.PageCount);
    }

    [Fact]
    public void FlushCurrentPage_WhileARequestIsInFlightSendsNothing()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));
        state.MarkRequestPending();

        Assert.Equal(
            RuntimeBookFlushAction.None, state.FlushCurrentPage("edit", out _));
        Assert.Equal("one", state.View.GetPage(0)!.Value.PageText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData(" \r\n ")]
    public void IsBlank_AcceptsWhitespaceOnlyPages(string text) =>
        Assert.True(RuntimeBookState.IsBlank(text));

    [Theory]
    [InlineData("a")]
    [InlineData("  .  ")]
    public void IsBlank_RejectsAnythingElse(string text) =>
        Assert.False(RuntimeBookState.IsBlank(text));

    [Fact]
    public void ResetSession_ClearsTheOpenBook()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one")));
        state.MarkRequestPending();

        state.ResetSession();

        RuntimeBookSnapshot snapshot = state.Snapshot;
        Assert.False(snapshot.IsOpen);
        Assert.Equal(0u, snapshot.BookGuid);
        Assert.Equal(0, snapshot.PageCount);
        Assert.Equal(-1, snapshot.CurrentPage);
        Assert.Equal(string.Empty, snapshot.Inscription);
        Assert.Equal(0u, snapshot.ScribeId);
        Assert.False(snapshot.RequestPending);
        Assert.Empty(state.View.Pages);
    }

    [Fact]
    public void ResetSession_OnAClosedBookIsANoOp()
    {
        RuntimeBookState state = NewState();
        long before = state.Snapshot.Revision;

        state.ResetSession();

        Assert.Equal(before, state.Snapshot.Revision);
    }

    [Fact]
    public void Revision_MovesOnEveryAcceptedChange()
    {
        RuntimeBookState state = NewState();
        long start = state.Snapshot.Revision;

        state.ApplyOpenBook(Open(Page(Player, "one"), Page(Player, "two")));
        long opened = state.Snapshot.Revision;
        Assert.True(opened > start);

        state.SetCurrentPage(1);
        Assert.True(state.Snapshot.Revision > opened);
    }

    [Fact]
    public void View_ReturnsPagesInOrder()
    {
        RuntimeBookState state = NewState();
        state.ApplyOpenBook(Open(Page(Player, "one"), Page(Other, "two")));

        IReadOnlyList<BookPage> pages = state.View.Pages;

        Assert.Equal(2, pages.Count);
        Assert.Equal("one", pages[0].PageText);
        Assert.Equal("two", pages[1].PageText);
    }
}
