using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.Core.Net.Messages;
using AcDream.Core.Quests;
using AcDream.Core.Ui;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.UI.Layout;

public sealed class JournalContractsPageController
{
    /// <summary>The layout the row template lives in — authored property
    /// <c>0x63</c> on the list's template entry.</summary>
    public const uint RowTemplateLayoutId = 0x21000069u;

    public const uint RowTemplateElementId = 0x100005D7u;

    private const uint ListId = 0x100005CFu;
    private const uint RowNameId = 0x100005D1u;
    private const uint RowStatusId = 0x100005D2u;

    private const uint StatusValueId = 0x100005DFu;
    private const uint ContactValueId = 0x100005E0u;
    private const uint ContactLocationValueId = 0x100005E1u;
    private const uint QuestLocationValueId = 0x100005E2u;
    private const uint DescriptionId = 0x100005DEu;
    private const uint TimedValueId = 0x100005E3u;
    private const uint AbandonButtonId = 0x100005DCu;

    public sealed record Bindings(
        IRuntimeContractView Contracts,
        Func<ContractCatalog> Catalog,
        Func<DateTime> Now,
        Func<uint, uint, UiElement?> TemplateResolver,
        Action<uint>? Abandon = null);

    private readonly Bindings _bindings;
    private readonly UiTemplateListBox? _list;
    private readonly UiText? _statusValue;
    private readonly UiText? _contactValue;
    private readonly UiText? _contactLocationValue;
    private readonly UiText? _questLocationValue;
    private readonly UiText? _description;
    private readonly UiText? _timedValue;

    private static readonly Vector4 SelectedNameColor = Vector4.One;

    private readonly Dictionary<UiText, UiTextLayoutCache<string>> _detailLayouts =
        new(ReferenceEqualityComparer.Instance);

    private readonly List<uint> _rowContractIds = [];
    private readonly List<(uint ContractId, UiText? Name, Vector4 Unselected)> _rows = [];

    private long _renderedRevision = -1;
    private uint _selectedContractId;

    public JournalContractsPageController(UiElement page, Bindings bindings)
    {
        ArgumentNullException.ThrowIfNull(page);
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));

        _list = UiElement.FindDescendant(page, ListId) as UiTemplateListBox;
        if (_list is not null)
            _list.TemplateResolver = bindings.TemplateResolver;

        _statusValue = UiElement.FindDescendant(page, StatusValueId) as UiText;
        _contactValue = UiElement.FindDescendant(page, ContactValueId) as UiText;
        _contactLocationValue =
            UiElement.FindDescendant(page, ContactLocationValueId) as UiText;
        _questLocationValue =
            UiElement.FindDescendant(page, QuestLocationValueId) as UiText;
        _description = UiElement.FindDescendant(page, DescriptionId) as UiText;
        _timedValue = UiElement.FindDescendant(page, TimedValueId) as UiText;

        if (UiElement.FindDescendant(page, AbandonButtonId) is UiButton abandon)
            abandon.OnClick = AbandonSelected;

        Refresh();
    }

    public uint SelectedContractId => _selectedContractId;

    public IReadOnlyList<uint> RowContractIds => _rowContractIds;

    public void Tick()
    {
        if (_bindings.Contracts.Snapshot.Revision != _renderedRevision)
            Refresh();
        else
            RefreshDetail();
    }

    public void Refresh()
    {
        RuntimeContractsSnapshot snapshot = _bindings.Contracts.Snapshot;
        _renderedRevision = snapshot.Revision;

        IReadOnlyList<ContractTracker> contracts = _bindings.Contracts.GetContracts();
        ContractCatalog catalog = _bindings.Catalog();
        DateTime now = _bindings.Now();

        if (snapshot.DisplayContractId != 0u)
            _selectedContractId = snapshot.DisplayContractId;
        if (_selectedContractId == 0u && contracts.Count != 0)
            _selectedContractId = contracts[0].ContractId;
        if (contracts.Count == 0)
            _selectedContractId = 0u;

        _rowContractIds.Clear();
        _rows.Clear();
        _list?.FlushPreservingScroll();

        foreach (ContractTracker tracker in contracts)
        {
            _rowContractIds.Add(tracker.ContractId);
            if (_list is null)
                continue;

            UiElement? row = _list.AddItemFromTemplateList(0);
            if (row is null)
                continue;

            ContractEntry entry = catalog.Lookup(tracker.ContractId);

            var name = UiElement.FindDescendant(row, RowNameId) as UiText;
            if (name is not null)
                SetText(name, entry.ContractName);
            if (UiElement.FindDescendant(row, RowStatusId) is UiText status)
            {
                SetText(status, ContractProgressText.Build(
                    (uint)tracker.Stage, tracker.TimeWhenRepeats,
                    tracker.ReceivedAt, entry, now));
            }

            _rows.Add((tracker.ContractId, name, name?.DefaultColor ?? Vector4.One));

            uint captured = tracker.ContractId;
            if (row is UiDatElement clickable)
            {
                clickable.ClickThrough = false;
                clickable.OnClick = () => Select(captured);
            }
        }

        ApplySelectionHighlight();
        RefreshDetail();
    }

    public void AbandonSelected()
    {
        if (_selectedContractId == 0u)
            return;

        _bindings.Abandon?.Invoke(_selectedContractId);
    }

    public void Select(uint contractId)
    {
        _selectedContractId = contractId;
        ApplySelectionHighlight();
        RefreshDetail();
    }

    /// <summary>
    /// Re-painted rather than merely remembered: a rebuild discards the old row
    /// objects, so a preserved selection has to be applied to the new ones.
    /// </summary>
    private void ApplySelectionHighlight()
    {
        foreach ((uint contractId, UiText? name, Vector4 unselected) in _rows)
        {
            if (name is not null)
            {
                name.DefaultColor =
                    contractId == _selectedContractId ? SelectedNameColor : unselected;
            }
        }
    }

    private void RefreshDetail()
    {
        ContractCatalog catalog = _bindings.Catalog();
        DateTime now = _bindings.Now();

        if (_selectedContractId == 0u
            || !_bindings.Contracts.TryGetContract(_selectedContractId, out ContractTracker tracker))
        {
            SetDetailText(_statusValue, string.Empty);
            SetDetailText(_contactValue, string.Empty);
            SetDetailText(_contactLocationValue, string.Empty);
            SetDetailText(_questLocationValue, string.Empty);
            SetDetailText(_description, string.Empty);
            SetDetailText(_timedValue, string.Empty);
            return;
        }

        ContractEntry entry = catalog.Lookup(_selectedContractId);

        SetDetailText(_statusValue, ContractProgressText.Build(
            (uint)tracker.Stage, tracker.TimeWhenRepeats, tracker.ReceivedAt, entry, now));
        SetDetailText(_contactValue, entry.NameNpcStart);
        SetDetailText(_contactLocationValue, LocationText(entry.LocationNpcStartCell));
        SetDetailText(_questLocationValue, LocationText(entry.LocationQuestAreaCell));
        SetDetailText(_description, entry.Description);

        SetDetailText(_timedValue, tracker.TimeWhenDone > 0d
            ? RetailDurationText.Format(
                Math.Max(0d, tracker.TimeWhenDone - (now - tracker.ReceivedAt).TotalSeconds))
            : string.Empty);
    }

    private static string LocationText(uint cellId)
    {
        if (cellId == 0u) return string.Empty;
        return RetailPositionFormatter.FormatOutdoorCell(cellId) ?? "Indoors";
    }

    private static void SetText(UiText? text, string value)
    {
        if (text is null) return;
        text.LinesProvider = () => [new UiText.Line(value, text.DefaultColor)];
    }

    /// <summary>
    /// Detail fields are rewritten on every tick, so they go through a layout
    /// cache: the line list is rebuilt only when the text, the element's width
    /// or its colour actually change, instead of a fresh closure and array per
    /// field per frame.
    /// </summary>
    private void SetDetailText(UiText? text, string value)
    {
        if (text is null) return;
        if (_detailLayouts.TryGetValue(text, out UiTextLayoutCache<string>? cache))
        {
            cache.SetValue(value);
            return;
        }

        cache = new UiTextLayoutCache<string>(
            text,
            static (target, content) => [new UiText.Line(content, target.DefaultColor)],
            value,
            StringComparer.Ordinal);
        _detailLayouts.Add(text, cache);
        text.LinesProvider = cache.Provider;
    }
}
