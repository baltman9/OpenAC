using System;
using System.Collections.Generic;
using System.Globalization;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

/// <summary>
/// The book reader. It is one of the main panel's authored slots rather
/// than a layout of its own, and it shows itself when the server answers
/// a Use on a book with the open-book event.
/// </summary>
public sealed class BookPanelController : IRetainedPanelController
{
    public const uint HostLayoutId = 0x2100006Eu;
    public const uint SlotElementId = 0x10000182u;

    public const uint TitleTextId = 0x1000010Fu;
    public const uint PageTextId = 0x10000111u;
    public const uint PreviousButtonId = 0x10000114u;
    public const uint NextButtonId = 0x10000115u;
    public const uint PageMenuId = 0x10000470u;
    public const uint PageNumberTextId = 0x1000047Bu;

    public sealed record Bindings(
        IRuntimeBookView Book,
        RuntimeBookState Commands,
        Func<uint, string> ResolveBookName,
        Action<uint /*bookGuid*/, int /*page*/> RequestPageText,
        Action<uint /*bookGuid*/> RequestAddPage,
        Action<bool> SetVisible,
        Action<uint /*bookGuid*/, int /*page*/, string /*text*/>? SavePage = null,
        Action<uint /*bookGuid*/, int /*page*/>? DeletePage = null);

    private readonly Bindings _bindings;
    private readonly UiText? _title;
    private readonly UiText? _pageTextDisplay;
    private readonly UiField? _pageTextField;
    private readonly UiText? _pageNumber;
    private readonly UiMenu? _pageMenu;

    private long _renderedRevision = -1;
    private bool _wasOpen;

    public UiElement Root { get; }

    private BookPanelController(UiElement root, Bindings bindings)
    {
        Root = root;
        _bindings = bindings;

        _title = UiElement.FindDescendant(root, TitleTextId) as UiText;
        _pageTextDisplay = UiElement.FindDescendant(root, PageTextId) as UiText;
        _pageTextField = UiElement.FindDescendant(root, PageTextId) as UiField;
        _pageNumber = UiElement.FindDescendant(root, PageNumberTextId) as UiText;
        _pageMenu = UiElement.FindDescendant(root, PageMenuId) as UiMenu;

        if (UiElement.FindDescendant(root, PreviousButtonId) is UiButton previous)
            previous.OnClick = () => Turn(CurrentPage - 1);
        if (UiElement.FindDescendant(root, NextButtonId) is UiButton next)
            next.OnClick = () => Turn(CurrentPage + 1);
        if (_pageMenu is not null)
        {
            _pageMenu.OnSelect = payload =>
            {
                if (payload is int page) Turn(page);
            };
        }

        Refresh();
    }

    /// <summary>
    /// Binds against an imported slot. Returns null when the slot did not
    /// carry the page text, which is the one child the reader cannot do
    /// without.
    /// </summary>
    public static BookPanelController? Bind(ImportedLayout layout, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(bindings);

        UiElement root = layout.Root;
        bool hasPageText =
            UiElement.FindDescendant(root, PageTextId) is UiText or UiField;
        return hasPageText ? new BookPanelController(root, bindings) : null;
    }

    /// <summary>For tests and hosts that already hold the built tree.</summary>
    public static BookPanelController? BindTo(UiElement root, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(bindings);
        bool hasPageText =
            UiElement.FindDescendant(root, PageTextId) is UiText or UiField;
        return hasPageText ? new BookPanelController(root, bindings) : null;
    }

    private int CurrentPage => _bindings.Book.Snapshot.CurrentPage;

    public void Tick()
    {
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        if (snapshot.Revision == _renderedRevision)
            return;

        Refresh();

        // A book opens itself when the server answers the Use, and takes
        // the panel down with it when the session drops it.
        if (snapshot.IsOpen != _wasOpen)
        {
            _wasOpen = snapshot.IsOpen;
            _bindings.SetVisible(snapshot.IsOpen);
        }
    }

    /// <summary>Hiding the panel closes the book, the way retail does.
    /// </summary>
    public void OnHidden()
    {
        // Closing a book saves the open page first, the same as turning
        // away from it.
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        if (TypedText() is { } typed)
        {
            RuntimeBookFlushAction flush =
                _bindings.Commands.FlushCurrentPage(typed, out int page);
            Send(snapshot.BookGuid, flush, page);
        }

        _bindings.Commands.CloseBook();
        _wasOpen = false;
        Refresh();
    }

    public void Dispose()
    {
    }

    public void Refresh()
    {
        RuntimeBookSnapshot snapshot = _bindings.Book.Snapshot;
        _renderedRevision = snapshot.Revision;

        // An inscribed book is titled by its inscription; anything else
        // wears the item's own name.
        SetText(
            _title,
            !snapshot.IsOpen
                ? string.Empty
                : snapshot.ScribeId != 0u
                    ? snapshot.Inscription
                    : _bindings.ResolveBookName(snapshot.BookGuid));

        BookPage? page = _bindings.Book.GetPage(snapshot.CurrentPage);
        string pageText = page?.TextIncluded != 0u
            ? page?.PageText ?? string.Empty
            : string.Empty;
        SetText(_pageTextDisplay, pageText);
        if (_pageTextField is not null)
        {
            _pageTextField.SetText(pageText);

            // A page is the reader's to write in when they wrote it, or
            // when the book lets anyone write; everything else is read
            // only, and so is a page whose text has not arrived yet.
            _pageTextField.Editable =
                snapshot.IsOpen
                && page is not null
                && page.Value.TextIncluded != 0u
                && _bindings.Book.IsPageEditable(snapshot.CurrentPage);
            // The book says how much fits on a page.
            if (snapshot.MaxNumCharsPerPage > 0)
                _pageTextField.MaxCharacters = snapshot.MaxNumCharsPerPage;
        }

        SetText(
            _pageNumber,
            snapshot.IsOpen && snapshot.CurrentPage >= 0
                ? (snapshot.CurrentPage + 1).ToString(CultureInfo.InvariantCulture)
                : string.Empty);

        RefreshMenu(snapshot);
    }

    /// <summary>
    /// The menu lists every page the book can hold, not only the ones
    /// written so far, so a writer can turn to the next blank slot.
    /// </summary>
    private void RefreshMenu(RuntimeBookSnapshot snapshot)
    {
        if (_pageMenu is null) return;

        if (!snapshot.IsOpen || snapshot.MaxNumPages <= 0)
        {
            _pageMenu.Items = Array.Empty<UiMenu.MenuItem>();
            _pageMenu.Selected = null;
            return;
        }

        var items = new UiMenu.MenuItem[snapshot.MaxNumPages];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new UiMenu.MenuItem(
                (i + 1).ToString(CultureInfo.InvariantCulture), i);
        }

        _pageMenu.Items = items;
        _pageMenu.Selected =
            snapshot.CurrentPage >= 0 && snapshot.CurrentPage < items.Length
                ? snapshot.CurrentPage
                : null;
    }

    private void Turn(int page)
    {
        RuntimeBookSnapshot before = _bindings.Book.Snapshot;
        RuntimeBookPageTurn turn = _bindings.Commands.TurnPage(page, TypedText());

        Send(before.BookGuid, turn.Flush, turn.FlushPage);

        switch (turn.Action)
        {
            case RuntimeBookPageAction.RequestPageText:
                _bindings.RequestPageText(before.BookGuid, turn.Page);
                break;
            case RuntimeBookPageAction.AddPage:
                _bindings.RequestAddPage(before.BookGuid);
                break;
            case RuntimeBookPageAction.Display:
            case RuntimeBookPageAction.None:
            default:
                break;
        }

        Refresh();
    }

    /// <summary>What the reader typed, or null when this book cannot be
    /// written in at all and so has nothing to save.</summary>
    private string? TypedText() =>
        _pageTextField is { Editable: true } field ? field.Text : null;

    private void Send(uint bookGuid, RuntimeBookFlushAction flush, int page)
    {
        switch (flush)
        {
            case RuntimeBookFlushAction.ModifyPage:
                _bindings.SavePage?.Invoke(
                    bookGuid, page, _pageTextField?.Text ?? string.Empty);
                break;
            case RuntimeBookFlushAction.DeletePage:
                _bindings.DeletePage?.Invoke(bookGuid, page);
                break;
            case RuntimeBookFlushAction.None:
            default:
                break;
        }
    }

    private static void SetText(UiText? text, string value)
    {
        if (text is null) return;
        text.LinesProvider = () => [new UiText.Line(value, text.DefaultColor)];
    }
}
