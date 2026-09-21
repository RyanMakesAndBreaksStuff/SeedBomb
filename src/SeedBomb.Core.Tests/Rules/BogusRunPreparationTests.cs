using SeedBomb.Core.Rules;
using System.Text.Json;

namespace SeedBomb.Core.Tests.Rules;

public class BogusRunPreparationTests
{
    [Fact]
    public async Task RiskyDescriptorWithoutOptIn_BlocksBeforeAnyGeneration()
    {
        var requests = Requests(("account", "email", "INTERNET", "email"));

        var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: false, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal(0, result.GeneratedValueCount);        // two-pass: nothing generated
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.RiskWarning);
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task LateOversizedRow_BlocksWholeRunAndCachesNothingUsable()
    {
        var requests = Requests(("account", "name", "COMPANY", "catchPhrase"), maxLength: 12);

        var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: true, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.LengthBudget);
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task RecordCountsBelowAndAbove32_AreExact()
    {
        foreach (var count in (int[])[10, 40])
        {
            var requests = Requests(("account", "name", "NAME", "firstName"), recordCount: count);
            var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: true, CancellationToken.None);

            Assert.False(result.IsBlocked);
            Assert.Equal(count, result.GeneratedValueCount);
            Assert.NotNull(result.Run);
            using var run = result.Run;
            var ctx = requests[0].Context;
            Assert.True(run.ContainsCache("account", "name"));
            Assert.IsType<string>(run.GetValue("account", "name", ctx, 0));
            Assert.IsType<string>(run.GetValue("account", "name", ctx, count - 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => run.GetValue("account", "name", ctx, count));
        }
    }

    [Fact]
    public async Task MaliciousAuthoredLength_IsRejectedBeforeAllocation()
    {
        var requests = Requests(
            ("account", "name", "RANDOM", "digits"),
            args: Args("length", 65_537),
            maxLength: 100_000);

        var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: true, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal(0, result.GeneratedValueCount);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.LengthBudget);
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task Cache_HoldsOnlyRulesRequiringExactPreparation()
    {
        var name = Request(("account", "name", "NAME", "firstName"));
        var ean = Request(("account", "ean", "COMMERCE", "ean8"));
        var digits = Request(("account", "code", "RANDOM", "digits"), args: Args("length", 8));

        var result = await BogusRulePreparer.PrepareRun([name, ean, digits], allowRiskyValues: true, CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Equal(10, result.GeneratedValueCount); // only firstName is preflighted
        using var run = result.Run!;
        Assert.True(run.ContainsCache("account", "name"));
        Assert.False(run.ContainsCache("account", "ean"));
        Assert.False(run.ContainsCache("account", "code"));
    }

    [Fact]
    public async Task ExampleEmail_NeverWarnsAndDoesNotNeedOptIn()
    {
        var requests = Requests(("account", "email", "INTERNET", "exampleEmail"));

        var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: false, CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.DoesNotContain(result.Messages, m => m.Code == RuleMessageCode.RiskWarning);
        using var run = result.Run!;
        Assert.True(run.ContainsCache("account", "email"));
    }

    [Fact]
    public async Task CanceledToken_ReleasesNothingUsable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BogusRulePreparer.PrepareRun(
                Requests(("account", "name", "NAME", "firstName")),
                allowRiskyValues: true,
                cts.Token));
    }

    [Fact]
    public async Task ContextAndLifetimeGuards_Hold()
    {
        var requests = Requests(("account", "name", "NAME", "firstName"));
        var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: true, CancellationToken.None);
        Assert.False(result.IsBlocked);
        var run = result.Run!;
        var ctx = requests[0].Context;

        Assert.Throws<InvalidOperationException>(() =>
            run.GetValue("account", "name", ctx with { Table = "contact" }, 0));
        Assert.Throws<InvalidOperationException>(() =>
            run.GetValue("account", "name", ctx with { RecordCount = 99 }, 0));

        run.Dispose();
        Assert.Throws<ObjectDisposedException>(() => run.ContainsCache("account", "name"));
        Assert.Throws<ObjectDisposedException>(() => run.GetValue("account", "name", ctx, 0));
    }

    [Fact]
    public async Task RiskyDescriptor_WithOptIn_Prepares()
    {
        var requests = Requests(("account", "email", "INTERNET", "email"));
        var result = await BogusRulePreparer.PrepareRun(requests, allowRiskyValues: true, CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.Contains(result.Messages, m => m.Code == RuleMessageCode.RiskWarning
                                           && m.Severity == RuleMessageSeverity.Warning);
        using var run = result.Run!;
        Assert.True(run.ContainsCache("account", "email"));
    }

    private static IReadOnlyList<BogusPreparationRequest> Requests(
        (string table, string column, string api, string endpoint) spec,
        int maxLength = 200,
        int recordCount = 10,
        IReadOnlyDictionary<string, JsonElement>? args = null) =>
        [Request(spec, maxLength, recordCount, args)];

    private static BogusPreparationRequest Request(
        (string table, string column, string api, string endpoint) spec,
        int maxLength = 200,
        int recordCount = 10,
        IReadOnlyDictionary<string, JsonElement>? args = null)
    {
        var attr = new StringAttributeMetadata { LogicalName = spec.column, MaxLength = maxLength };
        var ctx = new RuleEvaluationContext(spec.table, Seed: 42, Locale: "en", RunId: "r", RecordCount: recordCount);
        return new BogusPreparationRequest(
            new BogusRule(spec.api, spec.endpoint, 1, args),
            attr,
            spec.table,
            spec.column,
            ctx);
    }

    private static Dictionary<string, JsonElement> Args(params object[] pairs)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
            map[(string)pairs[i]] = JsonSerializer.SerializeToElement(pairs[i + 1]);
        return map;
    }
}
