using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Seedbomb.ViewModels.Controls;

/// <summary>Input-only lookup state; the parent retains Core validation and rule saving.</summary>
public sealed partial class LookupRuleInputViewModel : ObservableObject
{
    private LookupAttributeMetadata? _attribute;
    private string? _restoreError;
    private string? _entryError;

    /// <summary>Raised after an input changes, so the parent can revalidate.</summary>
    public event EventHandler? Changed;

    /// <summary>Identity-bearing entries in authored order.</summary>
    public ObservableCollection<LookupRuleValue> Records { get; } = [];

    /// <summary>Current metadata target names.</summary>
    public ObservableCollection<string> Targets { get; } = [];

    /// <summary>Target stamped onto the next manual GUID batch.</summary>
    [ObservableProperty] private string? _selectedTarget;

    /// <summary>Comma-separated GUID input that is not part of the rule until Add GUIDs.</summary>
    [ObservableProperty] private string _guidText = "";

    /// <summary>Creates local input state.</summary>
    public LookupRuleInputViewModel() => Records.CollectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnGuidTextChanged(string value)
    {
        _entryError = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedTargetChanged(string? value) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Clears state before a table/column changes or a rule is restored.</summary>
    public void Configure(LookupAttributeMetadata? attribute)
    {
        _attribute = attribute;
        _restoreError = null;
        _entryError = null;
        GuidText = "";
        Records.Clear();
        Targets.Clear();
        foreach (var target in (attribute?.Targets ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            Targets.Add(target);
        SelectedTarget = Targets.FirstOrDefault();
    }

    /// <summary>Restores identity JSON without fetching labels or changing list order.</summary>
    public void Restore(FieldRule rule)
    {
        Records.Clear();
        GuidText = "";
        _restoreError = null;
        _entryError = null;
        IEnumerable<JsonElement> values = rule switch
        {
            ConstantRule constant => new[] { constant.Value },
            OneOfRule oneOf => oneOf.Values,
            _ => Array.Empty<JsonElement>(),
        };
        foreach (var json in values)
        {
            if (_attribute is not null && LookupRuleValue.TryParse(json, _attribute, out var value, out var error))
                Records.Add(value!);
            else
                _restoreError =
                    "The saved lookup contains an invalid target or id. Clear or replace the selection to repair it.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Parses the GUID box as one batch stamped with the current target.</summary>
    [RelayCommand]
    private void AppendManual()
    {
        if (_attribute is null || SelectedTarget is null) return;
        var target = SelectedTarget.ToLowerInvariant();
        var tokens = GuidText.Split(',', StringSplitOptions.TrimEntries);
        var pending = new List<LookupRuleValue>();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (!Guid.TryParse(tokens[index], out var id) || id == Guid.Empty)
            {
                _entryError = $"GUID {index + 1} ('{tokens[index]}') is invalid. No records were added.";
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            pending.Add(new(target, id));
        }

        foreach (var value in pending)
            if (!Records.Any(v =>
                    v.Id == value.Id && string.Equals(v.Entity, value.Entity, StringComparison.OrdinalIgnoreCase)))
                Records.Add(value);
        GuidText = "";
        _entryError = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes one identity from the authored list.</summary>
    [RelayCommand]
    private void Remove(LookupRuleValue? value)
    {
        if (value is not null) Records.Remove(value);
    }

    /// <summary>Clears the authored list and uncommitted GUID text.</summary>
    [RelayCommand]
    private void Clear()
    {
        _restoreError = null;
        _entryError = null;
        GuidText = "";
        Records.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces temporary editor selection only after dialog Add.</summary>
    public void ReplaceSelection(IReadOnlyList<LookupRuleValue> values)
    {
        _restoreError = null;
        _entryError = null;
        GuidText = "";
        Records.Clear();
        foreach (var value in values) Records.Add(value with { });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds a draft; the parent must pass it through RuleValidator.</summary>
    public FieldRule? Build(string op, OneOfPick pick, out string? error)
    {
        error = null;
        if (op == "lookupRandom") return new LookupRandomRule();
        if (op == "null") return new NullRule();
        error = _restoreError ?? _entryError;
        if (error is not null) return null;
        if (!string.IsNullOrWhiteSpace(GuidText))
        {
            error = "Add the entered GUIDs to the selection, or clear the input, before saving.";
            return null;
        }

        if (op == "constant" && Records.Count == 1) return new ConstantRule(Records[0].ToJson());
        if (op == "oneOf" && Records.Count >= 2)
            return new OneOfRule(Array.AsReadOnly(Records.Select(v => v.ToJson()).ToArray()), pick);
        error = op == "constant"
            ? "Choose exactly one record for constant."
            : "Choose at least two records for one-of, or use constant for one record.";
        return null;
    }
}