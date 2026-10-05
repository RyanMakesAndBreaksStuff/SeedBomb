using SeedBomb.Core.Rules;
using SeedBomb.Services.Profiles;
using System.Text.Json;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class JsonProfileServiceTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    private JsonProfileService NewService(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "dg-profile-tests", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(root);
        return new JsonProfileService(root);
    }

    private async Task<string> WriteSourceFileAsync(string content)
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "dg-profile-tests-src", Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "import.profile.json");
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }

    private async Task<(Profile? Profile, string? Error)> ImportRawAsync(IProfileService svc, string json)
    {
        var path = await WriteSourceFileAsync(json);
        return await svc.ImportAsync(path, TestContext.Current.CancellationToken);
    }

    /// <summary>Deletes this instance's temp dirs (ponytail: per-test GUID dirs, no shared state to race).</summary>
    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 1. Round-trip idempotence: load -> save is byte-stable.
    [Fact]
    public async Task Round_trip_load_then_save_is_byte_stable()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        var profile = new Profile(1, "Acme Sales Scenario", "Sales seeding", 42,
        [
            new ProfileTable("account", 500, new Dictionary<string, FieldRule>
            {
                ["name"] = new PatternRule("ACME-{seq:0000} Test Account"),
                ["accountratingcode"] = new ConstantRule(J("1")),
            }),
        ]);

        await svc.SaveAsync(profile, ct);
        var path = Path.Combine(root, "acme-sales-scenario.profile.json");
        var firstBytes = await File.ReadAllBytesAsync(path, ct);

        var loaded = await svc.LoadAsync("acme-sales-scenario", ct);
        await svc.SaveAsync(loaded, ct);
        var secondBytes = await File.ReadAllBytesAsync(path, ct);

        Assert.Equal(firstBytes, secondBytes);
    }

    // 2. profileVersion greater than current is rejected with a "created by a newer version" message.
    [Fact]
    public async Task ProfileVersion_3_is_rejected_with_newer_version_message()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":3,"name":"Future Profile","tables":[{"table":"account","count":1}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains("created by a newer version", error);
        Assert.Contains("profileVersion 3", error);
    }

    [Theory]
    [InlineData("""{"name":"x","tables":[{"table":"account","count":1}]}""")]
    [InlineData("""{"profileVersion":0,"name":"x","tables":[{"table":"account","count":1}]}""")]
    [InlineData("""{"profileVersion":-1,"name":"x","tables":[{"table":"account","count":1}]}""")]
    public async Task Missing_zero_and_negative_profileVersion_are_unsupported(string json)
    {
        var svc = NewService(out _);
        var (profile, error) = await ImportRawAsync(svc, json);
        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains("unsupported profileVersion", error);
    }

    [Fact]
    public async Task V1ProfileContainingBogus_IsRejectedBeforeMigration()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"columns":{"name":{"op":"bogus","api":"NAME","endpoint":"firstName","engineVersion":1}}}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains("profileVersion 2", error);
        Assert.Contains("bogus", error);
    }

    [Fact]
    public async Task V1_loads_as_in_memory_v2_clone_and_import_persists_canonical_v2()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        var json = """
        {"profileVersion":1,"name":"Legacy","tables":[{"table":"account","count":1}]}
        """;

        var (imported, error) = await ImportRawAsync(svc, json);
        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal(Profile.CurrentProfileVersion, imported.ProfileVersion);

        var path = Path.Combine(root, "legacy.profile.json");
        var written = await File.ReadAllTextAsync(path, ct);
        Assert.Contains("\"profileVersion\": 2", written, StringComparison.Ordinal);

        var loaded = await svc.LoadAsync("legacy", ct);
        Assert.Equal(2, loaded.ProfileVersion);
    }

    [Fact]
    public async Task Duplicate_bogus_argument_property_is_rejected()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":2,"name":"x","tables":[{"table":"account","count":1,"columns":{"n":{"op":"bogus","api":"RANDOM","endpoint":"number","engineVersion":1,"args":{"min":1,"min":2}}}}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);
        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains("duplicate property", error);
    }

    [Fact]
    public async Task Every_writer_path_emits_current_profile_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        await svc.SaveAsync(new Profile(1, "Writer", null, null, [new ProfileTable("account", 1, null)]), ct);
        await svc.SaveDraftAsync(new Profile(1, "draft", null, null, [new ProfileTable("account", 1, null)]), ct);

        var saved = await File.ReadAllTextAsync(Path.Combine(root, "writer.profile.json"), ct);
        var draft = await File.ReadAllTextAsync(Path.Combine(root, "draft.profile.json"), ct);
        Assert.Contains("\"profileVersion\": 2", saved, StringComparison.Ordinal);
        Assert.Contains("\"profileVersion\": 2", draft, StringComparison.Ordinal);
    }

    // 3. Malformed JSON -> a single "not a valid profile: ..." line.
    [Fact]
    public async Task Malformed_json_is_rejected_with_single_line_message()
    {
        var svc = NewService(out _);

        var (profile, error) = await ImportRawAsync(svc, "{ this is not json");

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
        Assert.DoesNotContain("\n", error);
    }

    // 3b. Oversized JSON -> a single "not a valid profile: ..." line.
    [Fact]
    public async Task Oversized_file_is_rejected_with_single_line_message()
    {
        var svc = NewService(out _);
        var oversized = new string('x', 2 * 1024 * 1024); // 2 MiB, comfortably over the 1 MiB cap

        var (profile, error) = await ImportRawAsync(svc, oversized);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
        Assert.DoesNotContain("\n", error);
    }

    // 4. Unknown properties are rejected at every object level: top-level, table-level, rule-level.
    [Theory]
    [InlineData("""
    {"profileVersion":1,"name":"x","tables":[{"table":"account","count":1}],"unexpected":true}
    """)]
    [InlineData("""
    {"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"unexpected":true}]}
    """)]
    [InlineData("""
    {"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"columns":{"a":{"op":"constant","value":1,"unexpected":true}}}]}
    """)]
    public async Task Unknown_property_is_rejected_at_every_object_level(string json)
    {
        var svc = NewService(out _);

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
    }

    // 5. Reserved property names are rejected as property names, case-insensitively, at any level.
    [Theory]
    [InlineData("""{"profileVersion":1,"name":"x","connectionString":"bad","tables":[{"table":"account","count":1}]}""")]
    [InlineData("""{"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"clientSecret":"bad"}]}""")]
    [InlineData("""{"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"columns":{"a":{"op":"constant","value":1,"secret":"bad"}}}]}""")]
    [InlineData("""{"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"columns":{"PASSWORD":{"op":"constant","value":1}}}]}""")]
    [InlineData("""{"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"columns":{"a":{"op":"constant","value":1}}}],"Token":"bad"}""")]
    public async Task Reserved_property_name_is_rejected_case_insensitively(string json)
    {
        var svc = NewService(out _);

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
    }

    // 5b. The same reserved words inside a pattern-template *string value* remain allowed —
    // only property names are ever scanned.
    [Fact]
    public async Task Reserved_word_inside_pattern_template_value_is_allowed()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":1,"name":"Template Test","tables":[{"table":"account","count":1,"columns":{"description":{"op":"pattern","template":"password token secret clientSecret connectionString {seq}"}}}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(error);
        Assert.NotNull(profile);
    }

    // 6. Duplicate JSON properties are rejected, case-insensitively.
    [Theory]
    [InlineData("""{"profileVersion":1,"profileVersion":1,"name":"x","tables":[{"table":"account","count":1}]}""")]
    [InlineData("""{"profileVersion":1,"Name":"x","name":"y","tables":[{"table":"account","count":1}]}""")]
    [InlineData("""{"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"Count":2}]}""")]
    public async Task Duplicate_json_property_is_rejected_case_insensitively(string json)
    {
        var svc = NewService(out _);

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
    }

    // 7. Duplicate table names are rejected, case-insensitively.
    [Fact]
    public async Task Duplicate_table_names_are_rejected_case_insensitively()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":1,"name":"x","tables":[{"table":"Account","count":1},{"table":"account","count":2}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains("duplicate table", error, StringComparison.OrdinalIgnoreCase);
    }

    // 8. Duplicate column logical names are rejected, case-insensitively.
    [Fact]
    public async Task Duplicate_column_names_are_rejected_case_insensitively()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":1,"name":"x","tables":[{"table":"account","count":1,"columns":{"Name":{"op":"null"},"name":{"op":"null"}}}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
    }

    [Fact]
    public async Task ClearDraftAsync_removes_draft_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        await svc.SaveDraftAsync(new Profile(1, "Working Draft", null, 9, [new ProfileTable("account", 1, null)]), ct);
        Assert.True(File.Exists(Path.Combine(root, "draft.profile.json")));

        await svc.ClearDraftAsync(ct);
        Assert.Null(await svc.LoadDraftAsync(ct));
        await svc.ClearDraftAsync(ct);
    }

    // 9. draft.profile.json is excluded from ListAsync.
    [Fact]
    public async Task Draft_is_excluded_from_list()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        await svc.SaveAsync(new Profile(1, "Real Profile", null, null, [new ProfileTable("account", 1, null)]), ct);
        await svc.SaveDraftAsync(new Profile(1, "Working Draft", null, null, [new ProfileTable("account", 1, null)]), ct);

        var names = await svc.ListAsync(ct);

        Assert.Equal(["real-profile"], names);
    }

    // 10. Sanitized kebab file naming: "Acme Sales Scenario" -> acme-sales-scenario.profile.json.
    [Fact]
    public async Task Save_sanitizes_name_to_kebab_file_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);

        await svc.SaveAsync(new Profile(1, "Acme Sales Scenario", null, null, [new ProfileTable("account", 1, null)]), ct);

        Assert.True(File.Exists(Path.Combine(root, "acme-sales-scenario.profile.json")));
    }

    // 10b. Name prompts only accept names that are already their own file stem.
    [Theory]
    [InlineData("contact-acct", true)]
    [InlineData("g2", true)]
    [InlineData("Contact Acct", false)]
    [InlineData("contact acct", false)]
    [InlineData("contact-acct ", false)]
    [InlineData("Contact-Acct", false)]
    [InlineData("contact--acct", false)]
    [InlineData("-contact", false)]
    [InlineData("contact_acct", false)]
    [InlineData("draft", false)]
    [InlineData("", false)]
    public void IsValidName_accepts_only_names_equal_to_their_file_stem(string name, bool expected) =>
        Assert.Equal(expected, JsonProfileService.IsValidName(name));

    // 11. Saving under a name whose slug collides with a different existing profile's file is
    // rejected instead of silently overwriting it; the original file is left untouched.
    [Fact]
    public async Task SaveAsync_rejects_slug_collision_with_a_differently_named_profile()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        await svc.SaveAsync(new Profile(1, "Acme Sales", null, null, [new ProfileTable("account", 1, null)]), ct);
        var path = Path.Combine(root, "acme-sales.profile.json");
        var originalBytes = await File.ReadAllBytesAsync(path, ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SaveAsync(new Profile(1, "ACME   Sales", null, null, [new ProfileTable("account", 1, null)]), ct));

        Assert.Contains("Acme Sales", ex.Message);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, ct));
    }

    // 11b. Import of a differently-named profile that kebab-collides must not overwrite the original.
    [Fact]
    public async Task ImportAsync_rejects_slug_collision_with_a_differently_named_profile()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out var root);
        await svc.SaveAsync(new Profile(1, "Acme Sales", null, null, [new ProfileTable("account", 1, null)]), ct);
        var path = Path.Combine(root, "acme-sales.profile.json");
        var originalBytes = await File.ReadAllBytesAsync(path, ct);

        // "ACME   Sales" sanitizes to the same slug as "Acme Sales".
        var json = """
        {"profileVersion":1,"name":"ACME   Sales","tables":[{"table":"account","count":99}]}
        """;
        var (imported, error) = await ImportRawAsync(svc, json);

        Assert.Null(imported);
        Assert.NotNull(error);
        Assert.StartsWith("not a valid profile:", error);
        Assert.Contains("Acme Sales", error);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, ct));
    }

    // 11c. "draft" is reserved for autosave — Save must not clobber draft.profile.json.
    [Fact]
    public async Task SaveAsync_rejects_reserved_draft_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(new Profile(1, "Draft", null, null, [new ProfileTable("account", 1, null)]), ct));

        Assert.Contains("draft", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Smoke coverage for the remaining public surface: Import happy path, Delete, Duplicate, Export.

    [Fact]
    public async Task Import_valid_profile_is_saved_and_loadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        var json = """
        {"profileVersion":1,"name":"Imported Profile","tables":[{"table":"account","count":10,"columns":{"statuscode":{"op":"constant","value":1}}}]}
        """;

        var (imported, error) = await ImportRawAsync(svc, json);

        Assert.Null(error);
        Assert.NotNull(imported);
        Assert.Equal(["imported-profile"], await svc.ListAsync(ct));
        var loaded = await svc.LoadAsync("imported-profile", ct);
        Assert.Equal("Imported Profile", loaded.Name);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        await svc.SaveAsync(new Profile(1, "Temp Profile", null, null, [new ProfileTable("account", 1, null)]), ct);

        await svc.DeleteAsync("temp-profile", ct);

        Assert.Empty(await svc.ListAsync(ct));
    }

    [Fact]
    public async Task DuplicateAsync_saves_a_copy_under_the_new_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        await svc.SaveAsync(new Profile(1, "Original", "desc", 7, [new ProfileTable("account", 1, null)]), ct);

        var copy = await svc.DuplicateAsync("original", "Copy Of Original", ct);

        Assert.Equal("Copy Of Original", copy.Name);
        Assert.Equal(["copy-of-original", "original"], await svc.ListAsync(ct));
    }

    [Fact]
    public async Task ExportAsync_copies_the_stored_file_to_the_destination()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = NewService(out _);
        await svc.SaveAsync(new Profile(1, "Export Me", null, null, [new ProfileTable("account", 1, null)]), ct);
        var destPath = Path.Combine(Path.GetTempPath(), "dg-profile-tests-export", Guid.NewGuid().ToString("N") + ".profile.json");

        await svc.ExportAsync("export-me", destPath, ct);

        Assert.True(File.Exists(destPath));
    }

    [Fact]
    public async Task Save_that_cannot_complete_leaves_the_previous_file_whole()
    {
        // WR-008: new content goes to a sibling temp file that is swapped in, so the live file is
        // never truncated first. A blocked temp path stands in for a crash or a full disk.
        var svc = NewService(out var root);
        var ct = TestContext.Current.CancellationToken;
        await svc.SaveAsync(new Profile(1, "acme", null, 1, [new ProfileTable("account", 1, null)]), ct);
        var path = Path.Combine(root, "acme.profile.json");
        var before = await File.ReadAllTextAsync(path, ct);
        Directory.CreateDirectory(path + ".tmp");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.SaveAsync(new Profile(1, "acme", null, 2, [new ProfileTable("account", 5, null)]), ct));

        Assert.Equal(before, await File.ReadAllTextAsync(path, ct));
    }

    [Fact]
    public async Task Import_rejects_a_table_count_above_the_max_record_count()
    {
        var svc = NewService(out _);
        var json = """
        {"profileVersion":1,"name":"x","tables":[{"table":"account","count":2000000000}]}
        """;

        var (profile, error) = await ImportRawAsync(svc, json);

        Assert.Null(profile);
        Assert.NotNull(error);
        Assert.Contains("count must be at most", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_RejectsACountTheLoaderWouldReject()
    {
        // WR-014: a count-0 profile saved, then failed to load.
        using var svc = NewService(out _);
        var profile = new Profile(Profile.CurrentProfileVersion, "zero", null, 42,
            [new ProfileTable("account", 0, null)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SaveAsync(profile, TestContext.Current.CancellationToken));
        Assert.Contains("at least 1", ex.Message, StringComparison.Ordinal);
    }
}
