using System.IO;
using System.Text.Json;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.Services.Profiles;
using Seedbomb.ViewModels;
using Seedbomb.ViewModels.Controls;
using Xunit;

namespace DataGen.Wpf.Tests;

/// <summary>
/// Task 11 / P2 exit criterion: schema import + metadata validation → board state,
/// with BASE_CURRENCY / bad option / clamped range outcomes (Mock F5).
/// </summary>
public sealed class ProfileImportFlowTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private JsonProfileService NewService(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "dg-profile-import-tests", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(root);
        return new JsonProfileService(root);
    }

    private async Task<string> WriteSourceAsync(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dg-profile-import-src", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "import.profile.json");
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        return path;
    }

    /// <summary>
    /// Fabricated account metadata matching the hand-tooled §08-style profile used below.
    /// </summary>
    private static EntityMetadata BuildAccountMetadata()
    {
        var name = new StringAttributeMetadata
        {
            LogicalName = "name",
            IsValidForCreate = true,
            MaxLength = 100,
        };
        var employees = new IntegerAttributeMetadata
        {
            LogicalName = "numberofemployees",
            IsValidForCreate = true,
            MinValue = 0,
            MaxValue = 100,
        };
        // Mock F5 / Task 11: exchangerate → BASE_CURRENCY (FieldFilter + RuleEligibility).
        var exchangerate = new DecimalAttributeMetadata
        {
            LogicalName = "exchangerate",
            IsValidForCreate = false,
        };
        var options = new OptionSetMetadata();
        foreach (var v in new[] { 1, 2, 3, 4, 5 })
            options.Options.Add(new OptionMetadata(v));
        var preferred = new PicklistAttributeMetadata
        {
            LogicalName = "preferredcontactmethodcode",
            IsValidForCreate = true,
            OptionSet = options,
        };

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[]
        {
            name, employees, exchangerate, preferred,
        });
        return meta;
    }

    // Hand-tooled profile JSON (§08 example shape, adjusted to fabricated metadata + Task 11 cases).
    private const string HandTooledProfileJson = """
        {
          "profileVersion": 1,
          "name": "acme-sales-scenario",
          "description": "Sales seeding — controlled accounts",
          "seed": 42,
          "tables": [
            {
              "table": "account",
              "count": 500,
              "columns": {
                "name": { "op": "pattern", "template": "ACME-{seq:0000} Test Account" },
                "numberofemployees": { "op": "range", "min": -50, "max": 150 },
                "exchangerate": { "op": "constant", "value": 1.0 },
                "preferredcontactmethodcode": { "op": "constant", "value": 7 }
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task Import_plus_metadata_validation_matches_hand_built_board_and_partitions_outcomes()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        var path = await WriteSourceAsync(HandTooledProfileJson);

        // Layer 1 — schema (Task 10).
        var (profile, error) = await svc.ImportAsync(path, ct);
        Assert.Null(error);
        Assert.NotNull(profile);
        Assert.Equal("acme-sales-scenario", profile!.Name);

        // Layer 2 — metadata (Task 11).
        var meta = BuildAccountMetadata();
        var metadata = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = meta,
        };
        var report = ProfileImport.ValidateAgainstMetadata(profile, metadata, runId: "run-test");

        // Applied: name pattern + clamped range (2 rules).
        Assert.Equal(2, report.AppliedRuleCount);
        Assert.True(report.BoardRules.ContainsKey("account"));
        Assert.Equal(2, report.BoardRules["account"].Count);

        var nameRule = Assert.IsType<PatternRule>(report.BoardRules["account"]["name"]);
        Assert.Equal("ACME-{seq:0000} Test Account", nameRule.Template);

        var rangeRule = Assert.IsType<RangeRule>(report.BoardRules["account"]["numberofemployees"]);
        Assert.Equal(0m, rangeRule.Min.GetDecimal());
        Assert.Equal(100m, rangeRule.Max.GetDecimal());

        // Adjusted — out-of-bounds range clamped.
        Assert.NotEmpty(report.Adjusted);
        Assert.Contains(report.Adjusted, a =>
            a.Contains("clamped", StringComparison.OrdinalIgnoreCase)
            || a.Contains("numberofemployees", StringComparison.OrdinalIgnoreCase));

        // Not imported — exchangerate BASE_CURRENCY.
        Assert.Contains(report.NotImported, n =>
            n.Contains("exchangerate", StringComparison.OrdinalIgnoreCase)
            && n.Contains("BASE_CURRENCY", StringComparison.OrdinalIgnoreCase));

        // Not imported — option 7 on 1–5 optionset.
        Assert.Contains(report.NotImported, n =>
            n.Contains("preferredcontactmethodcode", StringComparison.OrdinalIgnoreCase)
            && n.Contains("valid values", StringComparison.OrdinalIgnoreCase)
            && n.Contains('7'));

        // Board state identical to building the same effective rules by hand.
        var handBoard = new FieldRulesViewModel();
        handBoard.SetRule("account", "name", nameRule, "name", "");
        handBoard.SetRule("account", "numberofemployees", rangeRule, "numberofemployees", "");

        var importBoard = new FieldRulesViewModel();
        var draft = new Dictionary<string, Dictionary<string, RuleDraftEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            ["account"] = report.BoardRules["account"].ToDictionary(
                kv => kv.Key,
                kv => new RuleDraftEntry(kv.Value, kv.Key, ""),
                StringComparer.OrdinalIgnoreCase),
        };
        importBoard.ReplaceDraft(draft);

        var hand = handBoard.GetRules();
        var imported = importBoard.GetRules();
        Assert.Equal(hand.Keys.Order(), imported.Keys.Order());
        Assert.Equal(hand["account"].Keys.Order(), imported["account"].Keys.Order());
        Assert.Equal(
            JsonSerializer.Serialize(hand["account"]["name"], FieldRule.JsonOptions),
            JsonSerializer.Serialize(imported["account"]["name"], FieldRule.JsonOptions));
        Assert.Equal(
            JsonSerializer.Serialize(hand["account"]["numberofemployees"], FieldRule.JsonOptions),
            JsonSerializer.Serialize(imported["account"]["numberofemployees"], FieldRule.JsonOptions));

        // ProfilesViewModel surfaces the same report via PresentImport (visual summary path).
        var vm = new ProfilesViewModel(svc)
        {
            GetMetadata = () => metadata,
            GetRunId = () => "run-test",
        };
        var presented = vm.PresentImport(profile, "import.profile.json");
        Assert.Equal(report.AppliedRuleCount, presented.AppliedRuleCount);
        Assert.True(vm.HasImportApplied);
        Assert.True(vm.HasImportAdjusted);
        Assert.True(vm.HasImportNotImported);
        Assert.Contains("BASE_CURRENCY", vm.ImportNotImportedMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("valid values", vm.ImportNotImportedMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_failure_sets_single_line_error_and_applies_nothing()
    {
        var svc = NewService(out _);
        var path = await WriteSourceAsync("{ not json");
        var vm = new ProfilesViewModel(svc)
        {
            GetMetadata = () => new Dictionary<string, EntityMetadata>(),
            GetRunId = () => "",
        };

        await vm.ImportFromPathAsync(path, TestContext.Current.CancellationToken);

        Assert.True(vm.HasSchemaError);
        Assert.Contains("not a valid profile", vm.SchemaErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(vm.PendingImport);
        Assert.False(vm.HasImportApplied);

        // Schema-error summary must still offer a way back to the manager list (then Close → rules).
        Assert.True(vm.ShowImportSummary);
        Assert.True(vm.DiscardImportCommand.CanExecute(null));
        vm.DiscardImportCommand.Execute(null);
        Assert.False(vm.ShowImportSummary);
        Assert.True(vm.ShowManager);
    }

    [Fact]
    public void Reason_code_BaseCurrency_is_BASE_CURRENCY()
    {
        Assert.Equal("BASE_CURRENCY", ProfileImport.ToReasonCode(EligibilityReason.BaseCurrency));
        Assert.Equal("PLATFORM_KEY", ProfileImport.ToReasonCode(EligibilityReason.PlatformKey));
    }
}
