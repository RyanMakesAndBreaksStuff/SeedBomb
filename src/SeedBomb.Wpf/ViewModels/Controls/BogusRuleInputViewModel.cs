using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DataGen.Core.Generators;
using DataGen.Core.Rules;

namespace Seedbomb.ViewModels.Controls;

/// <summary>Input-only Bogus catalog state; the parent retains Core validation and rule saving.</summary>
public sealed partial class BogusRuleInputViewModel : ObservableObject
{
    private bool _suppressCascade;

    /// <summary>Raised after an input changes, so the parent can revalidate.</summary>
    public event EventHandler? Changed;

    /// <summary>True while a programmatic restore is applying catalog fields.</summary>
    public bool IsSuppressing => _suppressCascade;

    [ObservableProperty] private IReadOnlyList<string> _bogusApis = [];
    [ObservableProperty] private string? _selectedBogusApi;
    [ObservableProperty] private IReadOnlyList<BogusEndpointOption> _bogusEndpoints = [];
    [ObservableProperty] private string? _selectedBogusEndpoint;
    [ObservableProperty] private bool _bogusHasNumericArgs;
    [ObservableProperty] private bool _bogusHasLengthArg;
    [ObservableProperty] private bool _bogusHasDateArgs;
    [ObservableProperty] private string _bogusMinNumber = "";
    [ObservableProperty] private string _bogusMaxNumber = "";
    [ObservableProperty] private string _bogusLengthText = "";
    [ObservableProperty] private DateTime? _bogusMinDate;
    [ObservableProperty] private DateTime? _bogusMaxDate;

    partial void OnSelectedBogusApiChanged(string? value)
    {
        BogusEndpoints = value is null || _kind is not { } kind
            ? []
            : BogusCatalogQuery.EndpointsFor(value, kind);
        if (_suppressCascade)
            return;
        SelectedBogusEndpoint = null;
        ClearAllArguments();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedBogusEndpointChanged(string? value)
    {
        if (_suppressCascade)
            return;
        ApplyArgumentVisibility(value);
        if (value is null)
            ClearAllArguments();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnBogusMinNumberChanged(string value)
    {
        if (!_suppressCascade) Changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnBogusMaxNumberChanged(string value)
    {
        if (!_suppressCascade) Changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnBogusLengthTextChanged(string value)
    {
        if (!_suppressCascade) Changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnBogusMinDateChanged(DateTime? value)
    {
        if (!_suppressCascade) Changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnBogusMaxDateChanged(DateTime? value)
    {
        if (!_suppressCascade) Changed?.Invoke(this, EventArgs.Empty);
    }

    private DataverseValueKind? _kind;

    /// <summary>Refreshes API/endpoint lists for <paramref name="kind"/>.</summary>
    public void RefreshCatalog(DataverseValueKind? kind)
    {
        _kind = kind;
        if (kind is not { } k)
        {
            BogusApis = [];
            BogusEndpoints = [];
            return;
        }

        BogusApis = BogusCatalogQuery.ApisFor(k);
        BogusEndpoints = SelectedBogusApi is null
            ? []
            : BogusCatalogQuery.EndpointsFor(SelectedBogusApi, k);
    }

    /// <summary>Restores a saved Bogus rule without cascading clears.</summary>
    public void Restore(BogusRule rule, DataverseValueKind? kind)
    {
        _suppressCascade = true;
        try
        {
            RefreshCatalog(kind);
            SelectedBogusApi = rule.Api;
            if (kind is { } k)
                BogusEndpoints = BogusCatalogQuery.EndpointsFor(rule.Api, k);
            SelectedBogusEndpoint = $"{rule.Api}.{rule.Endpoint}";
            ApplyArgumentVisibility(SelectedBogusEndpoint);
            RestoreArguments(rule);
        }
        finally
        {
            _suppressCascade = false;
        }
    }

    /// <summary>Builds a draft; the parent must pass it through RuleValidator.</summary>
    public FieldRule? TryBuild()
    {
        if (string.IsNullOrEmpty(SelectedBogusApi) || string.IsNullOrEmpty(SelectedBogusEndpoint))
            return null;

        var dot = SelectedBogusEndpoint.LastIndexOf('.');
        var endpoint = dot >= 0 ? SelectedBogusEndpoint[(dot + 1)..] : SelectedBogusEndpoint;
        return new BogusRule(SelectedBogusApi, endpoint, 1, BuildArgs());
    }

    /// <summary>Clears catalog selection and argument fields.</summary>
    public void ClearEditor()
    {
        _suppressCascade = true;
        try
        {
            SelectedBogusApi = null;
            SelectedBogusEndpoint = null;
            BogusApis = [];
            BogusEndpoints = [];
            ClearAllArguments();
            ApplyArgumentVisibility(null);
        }
        finally
        {
            _suppressCascade = false;
        }
    }

    private Dictionary<string, JsonElement>? BuildArgs()
    {
        if (BogusHasNumericArgs)
            return BuildNumericArgs();
        if (BogusHasLengthArg && !string.IsNullOrWhiteSpace(BogusLengthText)
            && TryParseJsonNumber(BogusLengthText, out var length))
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["length"] = length };
        if (BogusHasDateArgs && BogusMinDate is { } minDate && BogusMaxDate is { } maxDate)
            return BuildDateArgs(minDate, maxDate);
        return null;
    }

    private Dictionary<string, JsonElement>? BuildNumericArgs()
    {
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(BogusMinNumber) && TryParseJsonNumber(BogusMinNumber, out var min))
            args["min"] = min;
        if (!string.IsNullOrWhiteSpace(BogusMaxNumber) && TryParseJsonNumber(BogusMaxNumber, out var max))
            args["max"] = max;
        return args.Count == 0 ? null : args;
    }

    private static Dictionary<string, JsonElement> BuildDateArgs(DateTime minDate, DateTime maxDate) =>
        new(StringComparer.Ordinal)
        {
            ["min"] = JsonSerializer.SerializeToElement(
                DateOnly.FromDateTime(minDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ["max"] = JsonSerializer.SerializeToElement(
                DateOnly.FromDateTime(maxDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
        };

    private void RestoreArguments(BogusRule rule)
    {
        ClearAllArguments();
        if (rule.Args.TryGetValue("min", out var min))
        {
            if (BogusHasDateArgs && DateOnly.TryParse(min.GetString(), out var minDate))
                BogusMinDate = minDate.ToDateTime(TimeOnly.MinValue);
            else
                BogusMinNumber = ToEditorText(min);
        }

        if (rule.Args.TryGetValue("max", out var max))
        {
            if (BogusHasDateArgs && DateOnly.TryParse(max.GetString(), out var maxDate))
                BogusMaxDate = maxDate.ToDateTime(TimeOnly.MinValue);
            else
                BogusMaxNumber = ToEditorText(max);
        }

        if (rule.Args.TryGetValue("length", out var length))
            BogusLengthText = ToEditorText(length);
    }

    private void ApplyArgumentVisibility(string? endpointId)
    {
        var kind = BogusUiArgumentKind.None;
        if (SelectedBogusApi is not null && endpointId is not null)
        {
            var dot = endpointId.LastIndexOf('.');
            var endpoint = dot >= 0 ? endpointId[(dot + 1)..] : endpointId;
            kind = BogusCatalogQuery.ArgumentKind(SelectedBogusApi, endpoint);
        }

        BogusHasNumericArgs = kind == BogusUiArgumentKind.NumericRange;
        BogusHasLengthArg = kind == BogusUiArgumentKind.Length;
        BogusHasDateArgs = kind == BogusUiArgumentKind.DateRange;
    }

    private void ClearAllArguments()
    {
        BogusMinNumber = "";
        BogusMaxNumber = "";
        BogusLengthText = "";
        BogusMinDate = null;
        BogusMaxDate = null;
    }

    private static string ToEditorText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        _ => el.GetRawText(),
    };

    private static bool TryParseJsonNumber(string text, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Number)
            {
                element = doc.RootElement.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Malformed text is handed to the validator via a string element.
        }

        element = JsonSerializer.SerializeToElement(text);
        return true;
    }
}
