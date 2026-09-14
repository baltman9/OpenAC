using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;
using Xunit;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// These bind the controller against a tree built from the same element
/// ids the authored slot carries, and then drive it the way a reader
/// would: the point is that the wiring is live, not that the layout and
/// the state each work on their own.
/// </summary>
public sealed class BookPanelControllerTests
{
    private const uint Player = 0x5000000Au;
    private const uint Other = 0x5000000Bu;
    private const uint BookGuid = 0x80001234u;

    private sealed class Sent
    {
        public List<(uint Book, int Page)> PageTextRequests { get; } = [];
        public List<uint> AddPageRequests { get; } = [];
        public List<bool> Visibility { get; } = [];
        public List<(uint Book, int Page, string Text)> Saved { get; } = [];
        public List<(uint Book, int Page)> Deleted { get; } = [];
    }

    private static UiText Text(uint id) =>
        new() { DatElementId = id, Width = 160f, Height = 18f };

    private static UiField Field(uint id) =>
        new() { ElementId = id, DatElementId = id, Width = 200f, Height = 120f };

    private static UiButton Button(uint id) => new(
        new ElementInfo { Id = id, Type = 1, Width = 40, Height = 20 },
        static _ => (0u, 0, 0))
    {
        DatElementId = id,
    };

    private static UiElement BuildSlot()
    {
        var root = new UiPanel { Width = 400f, Height = 300f };
        root.AddChild(Text(BookPanelController.TitleTextId));
        root.AddChild(Field(BookPanelController.PageTextId));
        root.AddChild(Button(BookPanelController.PreviousButtonId));
        root.AddChild(Button(BookPanelController.NextButtonId));

        var menu = new UiMenu { DatElementId = BookPanelController.PageMenuId };
        menu.AddChild(Text(BookPanelController.PageNumberTextId));
        root.AddChild(menu);
        return root;
    }

    private static BookPage Page(
        uint author, string text, uint textIncluded = 1u, uint ignoreAuthor = 0u) =>
        new(author, "Someone", "acct", textIncluded, ignoreAuthor, text);

    private static BookEvents.OpenBook Open(
        int maxPages, uint scribeId, params BookPage[] pages) =>
        new(
            BookGuid,
            maxPages,
            new BookPageList(maxPages, 500, pages),
            "The Grand Inscription",
            scribeId,
            "Scribbler");

    private static (BookPanelController Controller, RuntimeBookState State,
        UiElement Root, Sent Sent) Bind()
    {
        var state = new RuntimeBookState(() => Player);
        var sent = new Sent();
        UiElement root = BuildSlot();

        BookPanelController? controller = BookPanelController.BindTo(
            root,
            new BookPanelController.Bindings(
                Book: state.View,
                Commands: state,
                ResolveBookName: _ => "Parchment",
                RequestPageText: (book, page) =>
                    sent.PageTextRequests.Add((book, page)),
                RequestAddPage: book => sent.AddPageRequests.Add(book),
                SetVisible: sent.Visibility.Add,
                SavePage: (book, page, text) => sent.Saved.Add((book, page, text)),
                DeletePage: (book, page) => sent.Deleted.Add((book, page))));

        Assert.NotNull(controller);
        return (controller!, state, root, sent);
    }

    private static string TextOf(UiElement root, uint id)
    {
        var text = (UiText?)UiElement.FindDescendant(root, id);
        Assert.NotNull(text);
        IReadOnlyList<UiText.Line>? lines = text!.LinesProvider?.Invoke();
        return lines is null || lines.Count == 0
            ? string.Empty
            : string.Join("\n", lines.Select(l => l.Text));
    }

    private static UiField PageField(UiElement root) =>
        (UiField)UiElement.FindDescendant(root, BookPanelController.PageTextId)!;

    private static UiMenu Menu(UiElement root) =>
        (UiMenu)UiElement.FindDescendant(root, BookPanelController.PageMenuId)!;

    private static void Click(UiElement root, uint id) =>
        ((UiButton)UiElement.FindDescendant(root, id)!).OnClick!();

    [Fact]
    public void Bind_ReturnsNullWhenTheSlotCarriesNoPageText()
    {
        var root = new UiPanel();
        root.AddChild(Text(BookPanelController.TitleTextId));

        BookPanelController? controller = BookPanelController.BindTo(
            root,
            new BookPanelController.Bindings(
                Book: new RuntimeBookState().View,
                Commands: new RuntimeBookState(),
                ResolveBookName: _ => string.Empty,
                RequestPageText: (_, _) => { },
                RequestAddPage: _ => { },
                SetVisible: _ => { }));

        Assert.Null(controller);
    }

    [Fact]
    public void OpeningABook_ShowsThePanelWithTheFirstPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "In the beginning")));
        controller.Tick();

        Assert.Equal([true], sent.Visibility);
        Assert.Equal("In the beginning", PageField(root).Text);
        Assert.Equal("1", TextOf(root, BookPanelController.PageNumberTextId));
    }

    [Fact]
    public void AnInscribedBookIsTitledByItsInscription()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "text")));
        controller.Tick();

        Assert.Equal(
            "The Grand Inscription", TextOf(root, BookPanelController.TitleTextId));
    }

    [Fact]
    public void AnUninscribedBookWearsTheItemName()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, scribeId: 0u, Page(Other, "text")));
        controller.Tick();

        Assert.Equal("Parchment", TextOf(root, BookPanelController.TitleTextId));
    }

    [Fact]
    public void TheNextButtonTurnsToAPageAlreadyInHand()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal(1, state.Snapshot.CurrentPage);
        Assert.Equal("two", PageField(root).Text);
        Assert.Equal("2", TextOf(root, BookPanelController.PageNumberTextId));
        Assert.Empty(sent.PageTextRequests);
        Assert.Empty(sent.AddPageRequests);
    }

    [Fact]
    public void TheNextButtonAsksForAPageThatArrivedWithoutItsText()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(
            4,
            Other,
            Page(Other, "one"),
            Page(Other, string.Empty, textIncluded: 0u)));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([(BookGuid, 1)], sent.PageTextRequests);
        Assert.Equal(string.Empty, PageField(root).Text);

        state.ApplyPageData(
            new BookEvents.PageDataResponse(BookGuid, 1, Page(Other, "fetched")));
        controller.Tick();

        Assert.Equal("fetched", PageField(root).Text);
    }

    [Fact]
    public void TheNextButtonAsksForANewPageOffTheEndOfAWritableBook()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([BookGuid], sent.AddPageRequests);
    }

    [Fact]
    public void TheNextButtonStopsAtTheEndOfABookThatCannotGrow()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(1, Other, Page(Other, "only page")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Empty(sent.AddPageRequests);
        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ThePreviousButtonTurnsBackAndStopsAtTheFirstPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();
        Click(root, BookPanelController.NextButtonId);

        Click(root, BookPanelController.PreviousButtonId);
        Assert.Equal(0, state.Snapshot.CurrentPage);

        Click(root, BookPanelController.PreviousButtonId);
        Assert.Equal(0, state.Snapshot.CurrentPage);
    }

    [Fact]
    public void ThePageMenuListsEverySlotTheBookCanHoldAndTracksTheOpenPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        UiMenu menu = Menu(root);
        Assert.Equal(4, menu.Items.Count);
        Assert.Equal(["1", "2", "3", "4"], menu.Items.Select(i => i.Label));
        Assert.Equal(0, menu.Selected);

        Click(root, BookPanelController.NextButtonId);
        Assert.Equal(1, Menu(root).Selected);
    }

    [Fact]
    public void SelectingFromThePageMenuTurnsToThatPage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        Menu(root).OnSelect!(1);

        Assert.Equal(1, state.Snapshot.CurrentPage);
        Assert.Equal("two", PageField(root).Text);
    }

    [Fact]
    public void HidingThePanelClosesTheBook()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        controller.OnHidden();

        Assert.False(state.Snapshot.IsOpen);
        Assert.Equal(string.Empty, PageField(root).Text);
        Assert.Equal(string.Empty, TextOf(root, BookPanelController.TitleTextId));
        Assert.Empty(Menu(root).Items);
    }

    [Fact]
    public void ClosingTheBookElsewhereTakesThePanelDown()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        state.ResetSession();
        controller.Tick();

        Assert.Equal([true, false], sent.Visibility);
    }

    [Fact]
    public void TickDoesNothingWhileTheBookHasNotChanged()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();
        controller.Tick();
        controller.Tick();

        Assert.Equal([true], sent.Visibility);
    }

    [Fact]
    public void ReOpeningTheSameBookDoesNotReRaiseVisibility()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "one")));
        controller.Tick();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "one"), Page(Other, "two")));
        controller.Tick();

        Assert.Equal([true], sent.Visibility);
    }
    [Fact]
    public void APageTheReaderWroteIsTypeable()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Player, "mine")));
        controller.Tick();

        Assert.True(PageField(root).Editable);
        Assert.Equal(500, PageField(root).MaxCharacters);
    }

    [Fact]
    public void APageSomeoneElseWroteIsReadOnly()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "theirs")));
        controller.Tick();

        Assert.False(PageField(root).Editable);
    }

    [Fact]
    public void ACommunalPageIsTypeableWhoeverWroteIt()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(Open(4, Other, Page(Other, "shared", ignoreAuthor: 1u)));
        controller.Tick();

        Assert.True(PageField(root).Editable);
    }

    [Fact]
    public void APageWhoseTextHasNotArrivedIsNotTypeable()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, _) = Bind();

        state.ApplyOpenBook(
            Open(4, Other, Page(Player, string.Empty, textIncluded: 0u)));
        controller.Tick();

        Assert.False(PageField(root).Editable);
    }

    [Fact]
    public void TurningAwayFromAnEditedPageSavesIt()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Player, "old"), Page(Player, "two")));
        controller.Tick();
        PageField(root).SetText("rewritten");

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([(BookGuid, 0, "rewritten")], sent.Saved);
        Assert.Equal("rewritten", state.View.GetPage(0)!.Value.PageText);
        Assert.Equal("two", PageField(root).Text);
    }

    [Fact]
    public void TurningAwayFromSomeoneElsesPageSavesNothing()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(
            Open(4, Other, Page(Other, "theirs"), Page(Other, "two")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);

        Assert.Empty(sent.Saved);
        Assert.Empty(sent.Deleted);
    }

    [Fact]
    public void BlankingYourOwnPageAndTurningForwardDeletesItAndFollowsTheShift()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(
            4, Other, Page(Player, "one"), Page(Player, "two"), Page(Player, "three")));
        controller.Tick();
        Click(root, BookPanelController.NextButtonId);
        PageField(root).SetText("   ");

        Click(root, BookPanelController.NextButtonId);

        Assert.Equal([(BookGuid, 1)], sent.Deleted);
        // Turning off page one saved it on the way past, unchanged --
        // every turn away from a writable page saves it.
        Assert.Equal([(BookGuid, 0, "one")], sent.Saved);
        Assert.Equal("three", PageField(root).Text);
        Assert.Equal(2, state.Snapshot.PageCount);
    }

    [Fact]
    public void ClosingThePanelSavesTheOpenPageFirst()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "old")));
        controller.Tick();
        PageField(root).SetText("final words");

        controller.OnHidden();

        Assert.Equal([(BookGuid, 0, "final words")], sent.Saved);
        Assert.False(state.Snapshot.IsOpen);
    }

    [Fact]
    public void ClosingThePanelOnAReadOnlyBookSavesNothing()
    {
        (BookPanelController controller, RuntimeBookState state,
            _, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Other, "lore")));
        controller.Tick();

        controller.OnHidden();

        Assert.Empty(sent.Saved);
        Assert.Empty(sent.Deleted);
    }

    [Fact]
    public void AddingAPageAndGettingTheAnswerLeavesABlankTypeablePage()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);
        Assert.Equal([BookGuid], sent.AddPageRequests);

        state.ApplyAddPageResponse(
            new BookEvents.PageResponse(BookGuid, 1, true), "Acdream");
        controller.Tick();

        Assert.Equal(string.Empty, PageField(root).Text);
        Assert.True(PageField(root).Editable);
        Assert.Equal(2, state.Snapshot.PageCount);
    }

    [Fact]
    public void NoTurnHappensWhileARequestIsStillInFlight()
    {
        (BookPanelController controller, RuntimeBookState state,
            UiElement root, Sent sent) = Bind();
        state.ApplyOpenBook(Open(4, Other, Page(Player, "one")));
        controller.Tick();

        Click(root, BookPanelController.NextButtonId);
        Click(root, BookPanelController.NextButtonId);

        Assert.Single(sent.AddPageRequests);
    }
}
