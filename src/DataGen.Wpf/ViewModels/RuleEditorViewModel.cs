using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace Seedbomb.ViewModels;

/// <summary>One row in the column picker — settable or platform-owned-with-reason (§3.2).</summary>
/// <param name="LogicalName">Attribute logical name.</param>
/// <param name="DisplayName">User-facing label.</param>
/// <param name="TypeLabel">Friendly type name shown in the picker row.</param>
/// <param name="IsSelectable">True = eligible rule target.</param>
/// <param name="DisabledReason">Copy shown when <paramref name="IsSelectable"/> is false; null when selectable.</param>
public sealed record PickerColumn(string LogicalName, string DisplayName, string TypeLabel,
    bool IsSelectable, string? DisabledReason);

/// <summary>One selectable operation chip in the rule editor (Mock F2 op cards).</summary>
/// <param name="Op">Wire op id (<c>constant</c>, <c>oneOf</c>, …).</param>
/// <param name="Title">Short label shown on the card.</param>
/// <param name="Hint">One-line description under the title.</param>
public sealed record OpOption(string Op, string Title, string Hint);

/// <summary>One checkable option for a Choice/Two-Options <c>oneOf</c> rule.</summary>
public sealed partial class OptionChoice : ObservableObject
{
    /// <summary>The option set value.</summary>
    public int Value { get; }

    /// <summary>The option's display label.</summary>
    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Initialises the option.</summary>
    public OptionChoice(int value, string label)
    {
        Value = value;
        Label = label;
    }
}

/// <summary>
/// ViewModel for the rule editor dialog — the one place settable vs. platform-owned columns
/// are enumerated (S3). Bodies delegate entirely to <see cref="RuleEligibility"/>,
/// <see cref="RuleValidator"/>, and <see cref="RuleValueGenerator"/> (Wave 1, DataGen.Core.Rules);
/// this class owns no eligibility, validation, or evaluation semantics of its own.
/// </summary>
public sealed partial class RuleEditorViewModel : ObservableObject
{
    // ── §3.1 Applies-to catalogs (Docs/field-rules-proto.html, table 3.1) ───────
    private static readonly string[] TextOps = ["constant", "oneOf", "pattern", "null"];
    private static readonly string[] NumericOps = ["constant", "oneOf", "range", "sequence", "null"]; // Whole#/BigInt/Decimal/Money
    private static readonly string[] FloatOps = ["constant", "oneOf", "range", "null"]; // Float has no sequence row
    private static readonly string[] DateOps = ["constant", "oneOf", "range", "null"];
    private static readonly string[] ChoiceOps = ["constant", "oneOf", "null"]; // Choice/Status(reason)/Two-Options
    private static readonly string[] NoOps = [];

    private readonly Dictionary<string, AttributeMetadata> _byName;
    private readonly List<PickerColumn> _allSettable;
    private readonly List<PickerColumn> _allExcluded;
    private readonly string _table;
    private readonly int _recordCount;
    private readonly int _seed;
    private readonly string _runId;

    private IReadOnlyList<RuleMessage> _messages = [];
    private IReadOnlyList<string> _previewValues = [];
    private FieldRule? _effectiveRule;

    /// <summary>Initialises the editor from full live entity metadata (Task 9 supplies this via <c>IMetadataProvider</c>).</summary>
    /// <param name="meta">Full entity metadata — editor never derives columns from <c>EntitySummary</c> or creates a provider.</param>
    /// <param name="recordCount">Planned record count for the run (sequence overflow / pattern worst-case width).</param>
    /// <param name="seed">Generation seed — feeds the deterministic preview substream.</param>
    /// <param name="runId">Run id — feeds <c>{runId}</c> pattern expansion and its worst-case length.</param>
    public RuleEditorViewModel(EntityMetadata meta, int recordCount, int seed, string runId)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(runId);

        _table = meta.LogicalName ?? string.Empty;
        _recordCount = recordCount;
        _seed = seed;
        _runId = runId;

        var attrs = meta.Attributes ?? [];
        _byName = attrs.Where(a => a.LogicalName is not null)
            .ToDictionary(a => a.LogicalName!, StringComparer.OrdinalIgnoreCase);

        // Alternate-key membership isn't visible to RuleEligibility.Classify (single-attribute
        // signature — see DataGen.Bulk.Tests/RuledGenerationTests.cs). BulkCreator rejects rules on
        // these columns unconditionally at preflight, so the picker excludes them too rather than
        // letting users configure a rule that can never be saved to a run.
        var altKeyAttrs = (meta.Keys ?? [])
            .SelectMany(k => k.KeyAttributes ?? [])
            .ToHashSet(StringComparer.Ordinal);

        _allSettable = [];
        _allExcluded = [];
        foreach (var attr in attrs)
        {
            var column = BuildPickerColumn(attr, altKeyAttrs);
            (column.IsSelectable ? _allSettable : _allExcluded).Add(column);
        }
    }

    /// <summary>DI constructor for the Rules page. Call <c>LoadForProfileAsync</c> on navigate.</summary>
    public RuleEditorViewModel()
    {
        _byName = new Dictionary<string, AttributeMetadata>(StringComparer.OrdinalIgnoreCase);
        _allSettable = [];
        _allExcluded = [];
        _table = string.Empty;
        _recordCount = 0;
        _seed = 0;
        _runId = string.Empty;
    }

    // ── Picker ────────────────────────────────────────────────────────────────

    /// <summary>Eligible rule targets (RuleEligibility.Classify == Settable), filtered by <see cref="SearchText"/>.</summary>
    public IReadOnlyList<PickerColumn> SettableColumns => Filter(_allSettable);

    /// <summary>Platform-owned columns, grouped and disabled with their reason; stays findable while excluded (S3).</summary>
    public IReadOnlyList<PickerColumn> ExcludedColumns => Filter(_allExcluded);

    [ObservableProperty]
    private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(SettableColumns));
        OnPropertyChanged(nameof(ExcludedColumns));
    }

    [ObservableProperty]
    private PickerColumn? _selectedColumn;

    partial void OnSelectedColumnChanged(PickerColumn? value)
    {
        Options.Clear();
        if (value is not null && _byName.TryGetValue(value.LogicalName, out var attr) && attr is EnumAttributeMetadata em)
        {
            foreach (var opt in em.OptionSet?.Options ?? [])
            {
                if (opt.Value is int v)
                {
                    var choice = new OptionChoice(v, opt.Label?.UserLocalizedLabel?.Label ?? v.ToString());
                    choice.PropertyChanged += (_, _) => Revalidate();
                    Options.Add(choice);
                }
            }
        }

        OnPropertyChanged(nameof(AvailableOps));
        OnPropertyChanged(nameof(AvailableOpOptions));
        SelectedOp = AvailableOps.FirstOrDefault() ?? string.Empty;
        Revalidate();
    }

    /// <summary>Ops valid for <see cref="SelectedColumn"/>'s type, per §3.1 Applies-to.</summary>
    public IReadOnlyList<string> AvailableOps =>
        SelectedColumn is not null && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
            ? OpsFor(attr)
            : NoOps;

    /// <summary>Op cards for the editor UI — same set as <see cref="AvailableOps"/> with display copy.</summary>
    public IReadOnlyList<OpOption> AvailableOpOptions => AvailableOps.Select(ToOpOption).ToList();

    // ── Op selection + typed parameters ──────────────────────────────────────

    [ObservableProperty]
    private string _selectedOp = string.Empty;

    partial void OnSelectedOpChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _constantText = string.Empty;
    partial void OnConstantTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _template = string.Empty;
    partial void OnTemplateChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _minText = string.Empty;
    partial void OnMinTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _maxText = string.Empty;
    partial void OnMaxTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _startText = string.Empty;
    partial void OnStartTextChanged(string value) => Revalidate();

    [ObservableProperty]
    private string _stepText = string.Empty;
    partial void OnStepTextChanged(string value) => Revalidate();

    /// <summary>Checkable option subset for Choice/Two-Options <c>oneOf</c> rules.</summary>
    public ObservableCollection<OptionChoice> Options { get; } = [];

    [ObservableProperty]
    private OneOfPick _pick = OneOfPick.Random;
    partial void OnPickChanged(OneOfPick value) => Revalidate();

    // ── Live validation + preview (RuleValidator / RuleValueGenerator — Wave 1) ─

    /// <summary>Live <see cref="RuleValidator"/> output for the current draft rule.</summary>
    public IReadOnlyList<RuleMessage> Messages => _messages;

    /// <summary>True when <see cref="Messages"/> is non-empty — drives the editor's <c>ui:InfoBar</c>.</summary>
    public bool HasMessages => _messages.Count > 0;

    /// <summary>First message's text, shown in the editor's <c>ui:InfoBar</c>.</summary>
    public string InfoBarMessage => _messages.Count > 0 ? _messages[0].Text : string.Empty;

    /// <summary>True when any current message is Error-severity — selects the InfoBar's Error styling.</summary>
    public bool InfoBarIsError => _messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <summary><see cref="RuleValueGenerator"/> output for rows 0..2 of the current effective rule.</summary>
    public IReadOnlyList<string> PreviewValues => _previewValues;

    /// <summary>True when a draft rule built and validated with no Error-severity message.</summary>
    public bool CanSave => _effectiveRule is not null && !_messages.Any(m => m.Severity == RuleMessageSeverity.Error);

    /// <summary>Returns the last-validated effective rule (clamped range, etc.), or null if nothing valid is drafted.</summary>
    public FieldRule? BuildRule() => _effectiveRule;

    /// <summary>
    /// Loads an existing board rule into the editor after <see cref="SelectedColumn"/> is set.
    /// Restores op + typed parameters so edit mode does not always fall back to the default op.
    /// </summary>
    public void ApplyExistingRule(FieldRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        switch (rule)
        {
            case ConstantRule c:
                SelectedOp = "constant";
                ConstantText = JsonElementToEditorText(c.Value);
                break;
            case OneOfRule o:
                SelectedOp = "oneOf";
                Pick = o.Pick;
                ApplyOneOfValues(o.Values);
                break;
            case RangeRule r:
                SelectedOp = "range";
                MinText = JsonElementToEditorText(r.Min);
                MaxText = JsonElementToEditorText(r.Max);
                break;
            case PatternRule p:
                SelectedOp = "pattern";
                Template = p.Template;
                break;
            case SequenceRule s:
                SelectedOp = "sequence";
                StartText = s.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                StepText = s.Step.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
            case NullRule:
                SelectedOp = "null";
                break;
        }
    }

    private void ApplyOneOfValues(IReadOnlyList<JsonElement> values)
    {
        if (Options.Count > 0)
        {
            var wanted = values
                .Select(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
                    : v.ValueKind == JsonValueKind.True ? 1
                    : v.ValueKind == JsonValueKind.False ? 0
                    : (int?)null)
                .Where(i => i.HasValue)
                .Select(i => i!.Value)
                .ToHashSet();
            foreach (var opt in Options)
                opt.IsChecked = wanted.Contains(opt.Value);
            return;
        }

        // Non-choice: comma-separated literal list reuses ConstantText.
        ConstantText = string.Join(", ", values.Select(JsonElementToEditorText));
    }

    private static string JsonElementToEditorText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        _ => el.GetRawText(),
    };

    private static OpOption ToOpOption(string op) => op switch
    {
        "constant" => new("constant", "constant", "fixed value on every row"),
        "oneOf" => new("oneOf", "one-of", "subset of options / literals"),
        "range" => new("range", "range", "uniform pick within bounds"),
        "pattern" => new("pattern", "pattern", "text template with tokens"),
        "sequence" => new("sequence", "sequence", "start + step per row"),
        "null" => new("null", "null", "leave unset (platform default)"),
        _ => new(op, op, string.Empty),
    };

    private void Revalidate()
    {
        List<RuleMessage> messages = [];
        List<string> preview = [];
        _effectiveRule = null;

        if (SelectedColumn is not null
            && _byName.TryGetValue(SelectedColumn.LogicalName, out var attr)
            && !string.IsNullOrEmpty(SelectedOp))
        {
            var draft = TryBuildDraft(attr, SelectedOp);
            if (draft is null)
            {
                messages.Add(new RuleMessage(RuleMessageSeverity.Error, "Enter a value for this rule."));
            }
            else
            {
                var result = RuleValidator.Validate(draft, attr, _recordCount, _runId);
                messages = result.Messages.ToList();
                if (result.IsValid && result.EffectiveRule is not null)
                {
                    _effectiveRule = result.EffectiveRule;
                    for (var row = 0; row < 3; row++)
                    {
                        var value = RuleValueGenerator.Evaluate(result.EffectiveRule, attr, _seed, _table, row, _runId);
                        preview.Add(FormatPreview(value));
                    }
                }
            }
        }

        _messages = messages;
        _previewValues = preview;
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(InfoBarMessage));
        OnPropertyChanged(nameof(InfoBarIsError));
        OnPropertyChanged(nameof(PreviewValues));
        OnPropertyChanged(nameof(CanSave));
    }

    private FieldRule? TryBuildDraft(AttributeMetadata attr, string op) => op switch
    {
        "constant" => TryConstantValue(attr, ConstantText, out var v) ? new ConstantRule(v) : null,
        "oneOf" => TryOneOf(attr),
        "range" => TryRange(attr),
        "pattern" => string.IsNullOrEmpty(Template) ? null : new PatternRule(Template),
        "sequence" => TrySequence(),
        "null" => new NullRule(),
        _ => null,
    };

    private FieldRule? TryOneOf(AttributeMetadata attr)
    {
        List<JsonElement> values;
        if (attr is EnumAttributeMetadata or BooleanAttributeMetadata)
        {
            values = Options.Where(o => o.IsChecked)
                .Select(o => JsonDocument.Parse(o.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement)
                .ToList();
        }
        else
        {
            // ponytail: literal-list entry for non-choice scalars piggybacks on ConstantText as a
            // comma-separated list — a dedicated multi-value editor is out of scope for this task.
            if (string.IsNullOrWhiteSpace(ConstantText)) return null;
            values = [];
            foreach (var token in ConstantText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryConstantValue(attr, token, out var v)) values.Add(v);
            }
        }
        return values.Count >= 2 ? new OneOfRule(values, Pick) : null;
    }

    private FieldRule? TryRange(AttributeMetadata attr)
    {
        if (string.IsNullOrWhiteSpace(MinText) || string.IsNullOrWhiteSpace(MaxText)) return null;
        return TryConstantValue(attr, MinText, out var min) && TryConstantValue(attr, MaxText, out var max)
            ? new RangeRule(min, max)
            : null;
    }

    private FieldRule TrySequence()
    {
        _ = decimal.TryParse(StartText, out var start);
        var step = decimal.TryParse(StepText, out var s) ? s : 1;
        return new SequenceRule(start, step);
    }

    private static bool TryConstantValue(AttributeMetadata attr, string? text, out JsonElement value)
    {
        value = default;
        if (text is null) return false;
        try
        {
            var json = attr switch
            {
                StringAttributeMetadata or MemoAttributeMetadata or DateTimeAttributeMetadata
                    => JsonSerializer.Serialize(text),
                BooleanAttributeMetadata => bool.Parse(text) ? "true" : "false",
                _ => text.Trim(),
            };
            value = JsonDocument.Parse(json).RootElement;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }

    private static string FormatPreview(object? value)
    {
        if (value is null) return "(null)";
        if (ReferenceEquals(value, RuleValueGenerator.Omit)) return "(omitted)";
        return value switch
        {
            OptionSetValue osv => osv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Money m => m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    // ── Picker copy (§3.2) ───────────────────────────────────────────────────

    private static PickerColumn BuildPickerColumn(AttributeMetadata attr, ISet<string> altKeyAttrs)
    {
        var name = attr.LogicalName ?? string.Empty;
        var display = attr.DisplayName?.UserLocalizedLabel?.Label ?? name;
        var eligibility = RuleEligibility.Classify(attr);

        if (eligibility.IsSettable && altKeyAttrs.Contains(name))
            return new PickerColumn(name, display, TypeLabelFor(attr), false, "Alternate key — rejected before generation begins.");

        return new PickerColumn(name, display, TypeLabelFor(attr), eligibility.IsSettable, ReasonText(eligibility.Reason));
    }

    // Reason copy sourced verbatim from the XML doc comments on EligibilityReason
    // (src/DataGen.Core/Rules/RuleEligibility.cs) — the authoritative §3.2 wording.
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
        EligibilityReason.Lookup => "Lookup — out of scope in v1.",
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
        LookupAttributeMetadata => "Lookup",
        UniqueIdentifierAttributeMetadata => "Unique Identifier",
        ImageAttributeMetadata or FileAttributeMetadata => "File/Image",
        _ => "Other",
    };

    private static IReadOnlyList<string> OpsFor(AttributeMetadata attr) => attr switch
    {
        StringAttributeMetadata or MemoAttributeMetadata => TextOps,
        IntegerAttributeMetadata or BigIntAttributeMetadata or DecimalAttributeMetadata or MoneyAttributeMetadata => NumericOps,
        DoubleAttributeMetadata => FloatOps,
        DateTimeAttributeMetadata => DateOps,
        EnumAttributeMetadata or BooleanAttributeMetadata => ChoiceOps,
        _ => NoOps,
    };

    private IReadOnlyList<PickerColumn> Filter(List<PickerColumn> source) =>
        string.IsNullOrWhiteSpace(SearchText)
            ? source
            : source.Where(c => c.LogicalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                              || c.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();
}
