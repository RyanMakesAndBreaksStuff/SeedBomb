using SeedBomb.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;

namespace SeedBomb.ViewModels;

/// <summary>Column picker lists, grouping, and chip filters for the rule editor.</summary>
public sealed class RuleColumnCatalog
{
    private readonly List<PickerColumn> _allSettable = [];
    private readonly List<PickerColumn> _allExcluded = [];
    private readonly List<PickerColumn> _allColumns = [];
    private string _filterKey = "All";

    /// <summary>Mapped / Required / Disabled / All chips.</summary>
    public ObservableCollection<ColumnFilterMode> FilterModes { get; } = [];

    /// <summary>Grouped view. Null until a profile-driven metadata load binds it.</summary>
    public ICollectionView? ColumnsView { get; private set; }

    /// <summary>Settable columns filtered by <paramref name="search"/>.</summary>
    public IReadOnlyList<PickerColumn> Settable(string search) => Filter(_allSettable, search);

    /// <summary>Platform-owned columns filtered by <paramref name="search"/>.</summary>
    public IReadOnlyList<PickerColumn> Excluded(string search) => Filter(_allExcluded, search);

    /// <summary>First settable column in metadata order, or null.</summary>
    public PickerColumn? FirstSettable => _allSettable.FirstOrDefault();

    /// <summary>Finds a column by logical name.</summary>
    public PickerColumn? Find(string logicalName) =>
        _allColumns.FirstOrDefault(c =>
            string.Equals(c.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Active Mapped / Required / Disabled / All chip key.</summary>
    public void SetFilterKey(string key) => _filterKey = key;

    /// <summary>True when <paramref name="column"/> passes the active search + chip filter.</summary>
    public bool Matches(
        PickerColumn column,
        string searchText,
        bool isRequired,
        bool isMapped,
        bool hasProfile)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!string.IsNullOrWhiteSpace(searchText)
            && !column.LogicalName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            && !column.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            return false;

        return _filterKey switch
        {
            "Required" => column.IsSelectable && isRequired,
            "Mapped" => column.IsSelectable && (isMapped || isRequired || !hasProfile),
            "Disabled" => !column.IsSelectable,
            _ => true,
        };
    }

    /// <summary>Rebuilds picker rows from <paramref name="meta"/> and fills <paramref name="byName"/>.</summary>
    public string Reset(
        EntityMetadata meta,
        Dictionary<string, AttributeMetadata> byName,
        Func<string, bool> isMapped,
        Func<PickerColumn, bool> isRequired)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(byName);
        ArgumentNullException.ThrowIfNull(isMapped);
        ArgumentNullException.ThrowIfNull(isRequired);

        var attrs = meta.Attributes ?? [];
        byName.Clear();
        foreach (var attr in attrs.Where(a => a.LogicalName is not null))
            byName[attr.LogicalName!] = attr;

        var altKeyAttrs = (meta.Keys ?? [])
            .SelectMany(k => k.KeyAttributes ?? [])
            .ToHashSet(StringComparer.Ordinal);

        _allSettable.Clear();
        _allExcluded.Clear();
        _allColumns.Clear();
        foreach (var attr in attrs)
        {
            var column = BuildPickerColumn(attr, altKeyAttrs, isMapped);
            (column.IsSelectable ? _allSettable : _allExcluded).Add(column);
            _allColumns.Add(column);
        }

        RebuildChips(isRequired);
        return meta.LogicalName ?? string.Empty;
    }

    /// <summary>Clears picker rows, chips, and the grouped view.</summary>
    public void Clear()
    {
        _allSettable.Clear();
        _allExcluded.Clear();
        _allColumns.Clear();
        FilterModes.Clear();
        ColumnsView = null;
    }

    /// <summary>Creates <see cref="ColumnsView"/> over the current column list (profile path only).</summary>
    public void BindView(Predicate<PickerColumn> matches)
    {
        ColumnsView = CollectionViewSource.GetDefaultView(_allColumns);
        ColumnsView.Filter = o => o is PickerColumn c && matches(c);
        ApplyGrouping();
    }

    /// <summary>Applies Mapped / Required / Unmapped / Disabled grouping.</summary>
    public void ApplyGrouping()
    {
        if (ColumnsView is not CollectionView view)
            return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(PickerColumn.GroupOrder),
                ListSortDirection.Ascending));
            view.SortDescriptions.Add(
                new SortDescription(nameof(PickerColumn.DisplayName), ListSortDirection.Ascending));
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PickerColumn.GroupName)));
        }
    }

    private void RebuildChips(Func<PickerColumn, bool> isRequired)
    {
        var mapped = _allSettable.Count(c => c.GroupName == "Mapped");
        var required = _allSettable.Count(isRequired);
        var disabled = _allExcluded.Count;
        var all = _allColumns.Count;
        FilterModes.Clear();
        FilterModes.Add(new ColumnFilterMode("Mapped", $"Mapped ({mapped})", mapped));
        FilterModes.Add(new ColumnFilterMode("Required", $"Required ({required})", required));
        FilterModes.Add(new ColumnFilterMode("Disabled", $"Disabled ({disabled})", disabled));
        FilterModes.Add(new ColumnFilterMode("All", $"All ({all})", all));
    }

    private static PickerColumn BuildPickerColumn(
        AttributeMetadata attr, ISet<string> altKeyAttrs, Func<string, bool> isMapped)
    {
        var name = attr.LogicalName ?? string.Empty;
        var display = attr.DisplayName?.UserLocalizedLabel?.Label ?? name;
        var eligibility = RuleEligibility.Classify(attr);
        var selectable = eligibility.IsSettable && !altKeyAttrs.Contains(name);
        var disabledReason = eligibility.IsSettable && altKeyAttrs.Contains(name)
            ? "Alternate key — rejected before generation begins."
            : ReasonText(eligibility.Reason);
        var required = IsRequiredLevel(attr);
        var mapped = selectable && isMapped(name);
        var (stateKey, groupName, groupOrder) = ResolveState(selectable, mapped, required);
        return new PickerColumn(name, display, TypeLabelFor(attr), selectable, disabledReason)
        {
            StateKey = stateKey,
            GroupName = groupName,
            GroupOrder = groupOrder,
        };
    }

    private static (string StateKey, string GroupName, int GroupOrder) ResolveState(
        bool selectable, bool mapped, bool required)
    {
        if (!selectable) return ("Disabled", "Disabled", 3);
        if (mapped) return ("Mapped", "Mapped", 0);
        if (required) return ("Required", "Unmapped · required", 1);
        return ("Unmapped", "Unmapped", 2);
    }

    private static IReadOnlyList<PickerColumn> Filter(List<PickerColumn> source, string search) =>
        string.IsNullOrWhiteSpace(search)
            ? source
            : source.Where(c => c.LogicalName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                || c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();

    private static bool IsRequiredLevel(AttributeMetadata attr) =>
        attr.RequiredLevel?.Value is AttributeRequiredLevel.SystemRequired
            or AttributeRequiredLevel.ApplicationRequired;

    // Reason copy sourced verbatim from the XML doc comments on EligibilityReason
    // (src/SeedBomb.Core/Rules/RuleEligibility.cs) — the authoritative §3.2 wording.
    private static string? ReasonText(EligibilityReason reason) => reason switch
    {
        EligibilityReason.Settable => null,
        EligibilityReason.PlatformKey => "Primary key — assigned by the platform.",
        EligibilityReason.AutoNumber => "Auto-numbered by platform.",
        EligibilityReason.Calculated => "Platform computes this value.",
        EligibilityReason.BaseCurrency => "Derived from exchange rate.",
        EligibilityReason.StateCode => "Platform-owned state — set Status (reason) instead.",
        EligibilityReason.BpfBookkeeping => "Platform-owned state — set Status (reason) instead.",
        EligibilityReason.BinaryUpload => "File/image — needs the upload API.",
        EligibilityReason.Lookup => "Unsupported lookup type or target metadata.",
        EligibilityReason.OwnerAssigned => "Owner is assigned by Dataverse; owner rules are not supported.",
        EligibilityReason.PolymorphicType => "Owner/Customer type — determined by its paired lookup value.",
        EligibilityReason.NotCreatable => "Not valid for create.",
        EligibilityReason.MultiSelectV2 => "MultiSelect — rule editing planned for v2.",
        _ => reason.ToString(),
    };

    private static string TypeLabelFor(AttributeMetadata attr) => attr switch
    {
        StringAttributeMetadata => "Text",
        MemoAttributeMetadata => "Memo",
        IntegerAttributeMetadata => "Whole Number",
        BigIntAttributeMetadata => "Big Integer",
        DecimalAttributeMetadata => "Decimal",
        DoubleAttributeMetadata => "Floating Point",
        MoneyAttributeMetadata => "Money",
        DateTimeAttributeMetadata => "Date/Time",
        BooleanAttributeMetadata => "Two Options",
        MultiSelectPicklistAttributeMetadata => "MultiSelect Choice",
        StatusAttributeMetadata => "Status (reason)",
        StateAttributeMetadata => "Status",
        EnumAttributeMetadata => "Choice",
        LookupAttributeMetadata lookup =>
            lookup.AttributeType == AttributeTypeCode.Customer ? "customer" : "lookup",
        UniqueIdentifierAttributeMetadata => "Unique Identifier",
        ImageAttributeMetadata or FileAttributeMetadata => "File/Image",
        _ => "Other",
    };
}