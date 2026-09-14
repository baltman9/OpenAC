using AcDream.App.UI;
using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The authored book slot, checked against the installed files. These
/// pin the two things a hand-built tree cannot see: that the page list
/// resolves to the drop-down widget with the art the layout gives it,
/// and that the page text resolves to a field that takes typing and
/// wraps inside its own rectangle.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class BookPanelLiveDatTests
{
    private static string DatDirectory =>
        Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");

    private static (ElementInfo Info, ImportedLayout Layout) ImportSlot()
    {
        using var dats = new DatCollection(DatDirectory, DatAccessType.Read);
        ElementInfo? slot = LayoutImporter.ImportInfos(
            dats,
            BookPanelController.HostLayoutId,
            BookPanelController.SlotElementId);
        Assert.NotNull(slot);
        return (slot!, LayoutImporter.Build(slot!, _ => (1u, 16, 16), null, null));
    }

    [Fact]
    public void PageListResolvesToTheDropDownWidget()
    {
        (ElementInfo slot, ImportedLayout layout) = ImportSlot();

        UiElement? element = layout.FindElement(BookPanelController.PageMenuId);
        UiMenu menu = Assert.IsType<UiMenu>(element);

        // A closed drop-down at rest, sat in the narrow row the layout
        // gives it rather than covering the page.
        Assert.False(menu.IsOpen);
        Assert.True(menu.RetailButtonArt);
        Assert.True(menu.Height <= 24f, $"page list is {menu.Height} tall");

        // It wears its own face and its own open/closed arrow.
        Assert.NotEqual(0u, menu.NormalSprite);
        Assert.NotEqual(0u, menu.ArrowCapClosedSprite);
        Assert.NotEqual(0u, menu.ArrowCapOpenSprite);

        ElementInfo menuInfo = Assert.Single(
            Flatten(slot), i => i.Id == BookPanelController.PageMenuId);
        Assert.Equal(6u, menuInfo.Type);
    }

    [Fact]
    public void PageTextResolvesToAWrappingTypeableField()
    {
        (ElementInfo slot, ImportedLayout layout) = ImportSlot();

        UiElement? element = layout.FindElement(BookPanelController.PageTextId);
        UiField field = Assert.IsType<UiField>(element);
        Assert.True(field.Editable);

        // The layout already marks it typeable; forcing the flag on must
        // not change that.
        ElementInfo pageText = Assert.Single(
            Flatten(slot), i => i.Id == BookPanelController.PageTextId);
        Assert.True(pageText.TryGetEffectiveBool(0x16u, out bool authoredEditable));
        Assert.True(authoredEditable);

        // Many lines of prose have to land inside the rectangle, not run
        // off the end of the first one.
        string paragraph = string.Join(
            ' ', Enumerable.Repeat("the letter runs on for a good while", 12));
        field.SetText(paragraph + "\n\n" + paragraph);

        Assert.Equal(paragraph.Length * 2 + 2, field.Text.Length);
        Assert.True(field.Width > 200f, $"page text is {field.Width} wide");
        Assert.True(field.Height > 120f, $"page text is {field.Height} tall");
    }

    [Fact]
    public void EveryChildTheReaderBindsToIsPresent()
    {
        (ElementInfo slot, _) = ImportSlot();
        var ids = Flatten(slot).Select(i => i.Id).ToHashSet();

        foreach (uint id in new[]
        {
            BookPanelController.TitleTextId,
            BookPanelController.PageTextId,
            BookPanelController.PreviousButtonId,
            BookPanelController.NextButtonId,
            BookPanelController.PageMenuId,
            BookPanelController.PageNumberTextId,
        })
        {
            Assert.True(ids.Contains(id), $"0x{id:X8} is not in the authored slot");
        }
    }

    private static IEnumerable<ElementInfo> Flatten(ElementInfo e)
    {
        yield return e;
        foreach (ElementInfo child in e.Children)
        {
            foreach (ElementInfo nested in Flatten(child))
                yield return nested;
        }
    }
}
