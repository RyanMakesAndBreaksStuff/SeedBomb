using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Generation;
using SeedBomb.Services.Profiles;
using SeedBomb.Services.Settings;
using SeedBomb.ViewModels;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Wpf.Tests.Views;
using System.Text.Json;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace SeedBomb.Wpf.Tests;

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
                kv => new RuleDraftEntry(kv.Value),
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

    [Fact]
    public void ValidateAgainstMetadata_BogusRule_UsesTableContext()
    {
        var meta = BuildAccountMetadata();
        var profile = new Profile(
            Profile.CurrentProfileVersion,
            "bogus-import",
            null,
            Seed: null,
            [
                new ProfileTable("account", 10, new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = new BogusRule("NAME", "firstName", 1),
                }),
            ]);

        var report = ProfileImport.ValidateAgainstMetadata(
            profile,
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = meta },
            runId: "run-test");

        Assert.Equal(1, report.AppliedRuleCount);
        Assert.IsType<BogusRule>(report.BoardRules["account"]["name"]);
        Assert.DoesNotContain(report.NotImported, n => n.Contains("validation context", StringComparison.OrdinalIgnoreCase));
    }

    // ── T8: ApplyImportReport selects the profile's tables ──────────────────────

    /// <summary>Builds a bare-bones view-model wired for T8 checks: no rules/counts needed on the report itself.</summary>
    private static async Task<(GenerateViewModel ViewModel, FieldOverridesViewModel Overrides)> NewViewModelWithMetadataAsync(
        IReadOnlyList<EntitySummary> preSelected, IReadOnlyList<EntityMetadata> metadata, CancellationToken ct)
    {
        var metadataMock = new Mock<IMetadataProvider>();
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var vm = new GenerateViewModel(
            Mock.Of<ISettingsService>(),
            Mock.Of<ISnackbarService>(),
            Mock.Of<ILogger<GenerateViewModel>>(),
            metadataMock.Object,
            Mock.Of<IProfileService>(),
            Mock.Of<IContentDialogService>(),
            new RunViewModel(Mock.Of<IWpfGenerationService>()));

        var overrides = new FieldOverridesViewModel();
        vm.AttachFieldOverrides(overrides);

        // Load metadata for the pre-selected tables (mirrors GoToRulesAsync — T1's fetch path)
        // so ApplyImportReport's SelectReportTables has EntityMetadata to build EntitySummary from.
        vm.OnEntitiesChanged(preSelected);
        await vm.GoToRulesCommand.ExecuteAsync(null);
        Assert.True(vm.IsRulesLoaded);

        return (vm, overrides);
    }

    private static ProfileImportReport ReportNaming(params (string Table, int Count)[] tables) =>
        new(
            BoardRules: new Dictionary<string, Dictionary<string, FieldRule>>(StringComparer.OrdinalIgnoreCase),
            TableCounts: tables.ToDictionary(t => t.Table, t => t.Count, StringComparer.OrdinalIgnoreCase),
            Seed: null,
            AppliedRuleCount: 0,
            AppliedTableSummaries: [.. tables.Select(t => $"{t.Table} ({t.Count})")],
            Adjusted: [],
            NotImported: []);

    [Fact]
    public async Task ApplyImportReport_selects_reports_tables_from_an_empty_starting_selection()
    {
        var ct = TestContext.Current.CancellationToken;
        var accountMeta = new EntityMetadata { LogicalName = "account" };
        var contactMeta = new EntityMetadata { LogicalName = "contact" };

        // Metadata for account + contact is already loaded (T1 EnsureMetadataAsync), but nothing
        // is selected on the board yet — the profiles-first scenario T8 exists to fix.
        var (vm, overrides) = await NewViewModelWithMetadataAsync(
            [new EntitySummary("account", "Account", false), new EntitySummary("contact", "Contact", false)],
            [accountMeta, contactMeta],
            ct);
        vm.OnEntitiesChanged([]);
        Assert.Empty(vm.SelectedEntities);

        var report = ReportNaming(("account", 500), ("contact", 300));
        vm.ApplyImportReport(report);

        Assert.Equal(
            new[] { "account", "contact" },
            vm.SelectedEntities.Select(e => e.LogicalName).Order(StringComparer.OrdinalIgnoreCase));
        var counts = overrides.GetCounts();
        Assert.Equal(500, counts["account"]);
        Assert.Equal(300, counts["contact"]);
    }

    [Fact]
    public async Task ApplyImportReport_does_not_duplicate_or_reorder_an_already_selected_table()
    {
        var ct = TestContext.Current.CancellationToken;
        var accountMeta = new EntityMetadata { LogicalName = "account" };
        var contactMeta = new EntityMetadata { LogicalName = "contact" };

        // contact then account — a specific, non-alphabetical order to prove it is preserved.
        var (vm, overrides) = await NewViewModelWithMetadataAsync(
            [new EntitySummary("contact", "Contact", false), new EntitySummary("account", "Account", false)],
            [accountMeta, contactMeta],
            ct);

        var report = ReportNaming(("account", 777));
        vm.ApplyImportReport(report);

        Assert.Equal(
            ["contact", "account"],
            vm.SelectedEntities.Select(e => e.LogicalName).ToArray());
        Assert.Equal(777, overrides.GetCounts()["account"]);
    }

    [Fact]
    public async Task Import_preserves_lookup_identity_name_order_cycle_and_optional_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        var json = """
            {
              "profileVersion": 2,
              "name": "lookup-roundtrip",
              "tables": [
                {
                  "table": "account",
                  "count": 10,
                  "columns": {
                    "parentcustomerid": {
                      "op": "oneOf",
                      "pick": "cycle",
                      "values": [
                        { "entity": "account", "id": "11111111-1111-1111-1111-111111111111", "name": "Acme" },
                        { "entity": "contact", "id": "22222222-2222-2222-2222-222222222222" }
                      ]
                    }
                  }
                }
              ]
            }
            """;
        var path = await WriteSourceAsync(json);
        var (profile, error) = await svc.ImportAsync(path, ct);
        Assert.Null(error);
        var oneOf = Assert.IsType<OneOfRule>(profile!.Tables[0].Columns!["parentcustomerid"]);
        Assert.Equal(OneOfPick.Cycle, oneOf.Pick);
        Assert.Equal(2, oneOf.Values.Count);
        Assert.Equal("Acme", oneOf.Values[0].GetProperty("name").GetString());
        Assert.False(oneOf.Values[1].TryGetProperty("name", out _));
        Assert.Equal("account", oneOf.Values[0].GetProperty("entity").GetString());
        Assert.Equal("contact", oneOf.Values[1].GetProperty("entity").GetString());

        var exportDir = Path.Combine(Path.GetTempPath(), "dg-profile-export", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(exportDir);
        Directory.CreateDirectory(exportDir);
        var exportPath = Path.Combine(exportDir, "exported.profile.json");
        await svc.ExportAsync(profile.Name, exportPath, ct);
        var svc2 = NewService(out _);
        var (reimported, reimportError) = await svc2.ImportAsync(exportPath, ct);
        Assert.Null(reimportError);
        var again = Assert.IsType<OneOfRule>(reimported!.Tables[0].Columns!["parentcustomerid"]);
        Assert.Equal(OneOfPick.Cycle, again.Pick);
        Assert.Equal("account", again.Values[0].GetProperty("entity").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", again.Values[0].GetProperty("id").GetString());
        Assert.Equal("Acme", again.Values[0].GetProperty("name").GetString());
        Assert.Equal("contact", again.Values[1].GetProperty("entity").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", again.Values[1].GetProperty("id").GetString());
        Assert.False(again.Values[1].TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Import_unknown_op_is_rejected()
    {
        var svc = NewService(out _);
        var path = await WriteSourceAsync("""
            {
              "profileVersion": 2,
              "name": "bad-op",
              "tables": [
                {
                  "table": "account",
                  "count": 1,
                  "columns": {
                    "parentaccountid": { "op": "oneOfPicker", "values": [] }
                  }
                }
              ]
            }
            """);
        var (profile, error) = await svc.ImportAsync(path, TestContext.Current.CancellationToken);
        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
    }

    [Fact]
    public async Task Import_lookupRandom_is_accepted_on_current_binary()
    {
        var svc = NewService(out _);
        var path = await WriteSourceAsync("""
            {
              "profileVersion": 2,
              "name": "lookup-random",
              "tables": [
                {
                  "table": "account",
                  "count": 1,
                  "columns": {
                    "parentaccountid": { "op": "lookupRandom" }
                  }
                }
              ]
            }
            """);
        var (profile, error) = await svc.ImportAsync(path, TestContext.Current.CancellationToken);
        Assert.Null(error);
        Assert.IsType<LookupRandomRule>(profile!.Tables[0].Columns!["parentaccountid"]);
    }

    [Fact]
    public void Owner_hand_edited_into_profile_is_rejected_by_preflight()
    {
        var owner = new LookupAttributeMetadata
        {
            LogicalName = "ownerid",
            IsValidForCreate = true,
            Targets = ["systemuser"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(owner, AttributeTypeCode.Owner);
        var meta = BuildAccountMetadata();
        meta.GetType().GetProperty("Attributes")!
            .SetValue(meta, meta.Attributes.Append(owner).ToArray());
        var profile = new Profile(
            Profile.CurrentProfileVersion,
            "owner-edit",
            null,
            42,
            [
                new ProfileTable("account", 10, new Dictionary<string, FieldRule>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ownerid"] = new ConstantRule(
                        new LookupRuleValue("systemuser", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).ToJson()),
                }),
            ]);

        var report = ProfileImport.ValidateAgainstMetadata(
            profile,
            new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase) { ["account"] = meta },
            runId: "run-test");

        Assert.Equal(0, report.AppliedRuleCount);
        Assert.Contains(report.NotImported, n =>
            n.Contains("ownerid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Import_loads_live_metadata_and_asks_before_replacing_a_dirty_board()
    {
        // CR-005: import used to validate against whatever Generate had cached (empty on a fresh
        // session, so every table read "not available") and skipped the dirty-board prompt.
        var cache = new Dictionary<string, EntityMetadata>(StringComparer.OrdinalIgnoreCase);
        string? prompt = null;
        var vm = new ProfilesViewModel(NewService(out _))
        {
            GetMetadata = () => cache,
            EnsureMetadata = (tables, _) =>
            {
                foreach (var table in tables)
                    cache[table] = BuildAccountMetadata();
                return Task.CompletedTask;
            },
            IsBoardDirty = () => true,
            ConfirmOverwrite = message => { prompt = message; return true; },
        };

        await vm.ImportFromPathAsync(await WriteSourceAsync(HandTooledProfileJson), TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.PendingImport?.AppliedRuleCount);
        Assert.NotNull(prompt);
    }

    [Fact]
    public async Task Import_presents_nothing_when_the_user_keeps_a_dirty_board()
    {
        var vm = new ProfilesViewModel(NewService(out _))
        {
            GetMetadata = () => new Dictionary<string, EntityMetadata>(),
            IsBoardDirty = () => true,
            ConfirmOverwrite = _ => false,
        };

        await vm.ImportFromPathAsync(await WriteSourceAsync(HandTooledProfileJson), TestContext.Current.CancellationToken);

        Assert.Null(vm.PendingImport);
    }

    [StaFact]
    public void Dirty_board_import_asks_through_the_themed_dialog()
    {
        // WR-020: with ProfilesPage's MessageBox override gone and no seam wired, a dirty board
        // must still be confirmed, not overwritten silently.
        // ConfirmAsync builds a ContentDialog after the import's file awaits. StaFact starts on
        // an STA thread but does not pump one, so keep those continuations here.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var dialogs = new Mock<IContentDialogService>();
        dialogs.Setup(d => d.ShowAsync(It.IsAny<ContentDialog>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentDialogResult.None);
        var vm = new ProfilesViewModel(NewService(out _), dialogs: dialogs.Object)
        {
            GetMetadata = () => new Dictionary<string, EntityMetadata>(),
            IsBoardDirty = () => true,
        };

        var write = WriteSourceAsync(HandTooledProfileJson);
        Wait(write);
        var import = vm.ImportFromPathAsync(write.GetAwaiter().GetResult(), TestContext.Current.CancellationToken);
        Wait(import);

        dialogs.Verify(d => d.ShowAsync(It.IsAny<ContentDialog>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(vm.PendingImport);

        static void Wait(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(
                    _ => frame.Continue = false,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);
            }

            task.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Import_of_a_same_name_profile_confirms_before_overwriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        await svc.SaveAsync(new Profile(1, "acme-sales", null, null, [new ProfileTable("account", 1, null)]), ct);
        var path = Path.Combine(root, "acme-sales.profile.json");
        var before = await File.ReadAllTextAsync(path, ct);

        string? prompt = null;
        var vm = new ProfilesViewModel(svc)
        {
            GetMetadata = () => new Dictionary<string, EntityMetadata>(),
            ConfirmOverwrite = message => { prompt = message; return true; },
        };

        var json = """{"profileVersion":1,"name":"acme-sales","tables":[{"table":"account","count":5}]}""";
        await vm.ImportFromPathAsync(await WriteSourceAsync(json), ct);

        Assert.NotNull(prompt);
        var after = await File.ReadAllTextAsync(path, ct);
        Assert.NotEqual(before, after);
        Assert.Contains("\"count\": 5", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromPathAsync_ShowsImportSummary_WhenTheStoreThrows()
    {
        var profiles = new Mock<IProfileService>();
        // Task 7 added the optional allowOverwrite parameter; expression trees cannot omit it (CS0854).
        profiles.Setup(p => p.ImportAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ThrowsAsync(new IOException("import.profile.json is locked"));

        var vm = new ProfilesViewModel(profiles.Object)
        {
            GetMetadata = () => new Dictionary<string, EntityMetadata>(),
        };

        await vm.ImportFromPathAsync(@"C:\temp\import.profile.json", TestContext.Current.CancellationToken);

        Assert.True(vm.ShowImportSummary);
        Assert.Equal("import.profile.json is locked", vm.SchemaErrorMessage);
    }
}
