using SeedBomb.Core.Rules;
using System.Text.Json;

namespace SeedBomb.Core.Tests.Rules;

public class BogusEvaluatorTests
{
    private const string ExpectedRow0 = "Kurtis";

    [Fact]
    public void SeedFraming_MatchesFixedVector()
    {
        var ctx = new RuleEvaluationContext("account", Seed: 42, Locale: "en", RunId: "r", RecordCount: 10);
        var prepared = BogusRulePreparer.CompileRule(
            new BogusRule("NAME", "firstName", 1), StringAttr("name"), ctx);

        using var session = new BogusEvaluatorSession(ctx.Locale);
        var row0 = session.Evaluate(prepared, StringAttr("name"), ctx, rowIndex: 0);
        var row1 = session.Evaluate(prepared, StringAttr("name"), ctx, rowIndex: 1);

        Assert.Equal(ExpectedRow0, row0);           // fixed vector, captured once and frozen
        Assert.NotEqual(row0, session.Evaluate(prepared, StringAttr("name"),
            ctx with { Table = "contact" }, rowIndex: 0));   // tuple separation
        Assert.Equal(row1, session.Evaluate(prepared, StringAttr("name"),
            ctx with { RecordCount = 5000 }, rowIndex: 1));  // count does not perturb rows
    }

    [Fact]
    public void TupleSeparation_VariesSeedTableColumnApiEndpointAndRow()
    {
        var ctx = Ctx();
        using var session = new BogusEvaluatorSession(ctx.Locale);
        var name = Compile("NAME", "firstName", StringAttr("name"), ctx);
        var baseline = session.Evaluate(name, StringAttr("name"), ctx, 0);

        Assert.NotEqual(baseline, session.Evaluate(name, StringAttr("name"), ctx with { Seed = 43 }, 0));
        Assert.NotEqual(baseline, session.Evaluate(name, StringAttr("name"), ctx with { Table = "contact" }, 0));
        Assert.NotEqual(baseline, session.Evaluate(
            Compile("NAME", "firstName", StringAttr("firstname"), ctx), StringAttr("firstname"), ctx, 0));
        Assert.NotEqual(baseline, session.Evaluate(
            Compile("NAME", "lastName", StringAttr("name"), ctx), StringAttr("name"), ctx, 0));
        Assert.NotEqual(baseline, session.Evaluate(
            Compile("COMPANY", "companyName", StringAttr("name"), ctx), StringAttr("name"), ctx, 0));
        Assert.NotEqual(baseline, session.Evaluate(name, StringAttr("name"), ctx, 1));
    }

    [Fact]
    public void ForwardAndReverseRowVectors_MatchByRowKey()
    {
        var ctx = Ctx();
        var prepared = Compile("NAME", "firstName", StringAttr("name"), ctx);
        using var session = new BogusEvaluatorSession(ctx.Locale);

        var forward = new Dictionary<int, object>();
        for (var i = 0; i < 8; i++)
            forward[i] = session.Evaluate(prepared, StringAttr("name"), ctx, i);

        var reverse = new Dictionary<int, object>();
        for (var i = 7; i >= 0; i--)
            reverse[i] = session.Evaluate(prepared, StringAttr("name"), ctx, i);

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void RunIdAndRecordCount_DoNotPerturbRows()
    {
        var ctx = Ctx();
        var prepared = Compile("NAME", "firstName", StringAttr("name"), ctx);
        using var session = new BogusEvaluatorSession(ctx.Locale);
        var baseline = session.Evaluate(prepared, StringAttr("name"), ctx, 3);

        Assert.Equal(baseline, session.Evaluate(prepared, StringAttr("name"), ctx with { RunId = "other" }, 3));
        Assert.Equal(baseline, session.Evaluate(prepared, StringAttr("name"), ctx with { RecordCount = 99 }, 3));
    }

    [Fact]
    public void NegativeRow_IsRejected()
    {
        var ctx = Ctx();
        var prepared = Compile("NAME", "firstName", StringAttr("name"), ctx);
        using var session = new BogusEvaluatorSession(ctx.Locale);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            session.Evaluate(prepared, StringAttr("name"), ctx, -1));
    }

    [Fact]
    public void ResultTypes_MatchSdkContracts()
    {
        var ctx = Ctx();
        using var session = new BogusEvaluatorSession(ctx.Locale);

        Assert.IsType<string>(Eval(session, "NAME", "firstName", StringAttr("name"), ctx));
        Assert.IsType<bool>(Eval(session, "RANDOM", "bool", new BooleanAttributeMetadata { LogicalName = "donotemail" }, ctx));
        Assert.IsType<int>(Eval(session, "RANDOM", "number",
            new IntegerAttributeMetadata { LogicalName = "n", MinValue = 0, MaxValue = 100 }, ctx));
        Assert.IsType<long>(Eval(session, "RANDOM", "long",
            new BigIntAttributeMetadata { LogicalName = "n" }, ctx));
        Assert.IsType<decimal>(Eval(session, "RANDOM", "decimal",
            new DecimalAttributeMetadata { LogicalName = "d", Precision = 2 }, ctx));
        Assert.IsType<double>(Eval(session, "RANDOM", "double",
            new DoubleAttributeMetadata { LogicalName = "f" }, ctx));
        Assert.IsType<Money>(Eval(session, "FINANCE", "amount",
            new MoneyAttributeMetadata { LogicalName = "rev", Precision = 2 }, ctx));
        Assert.IsType<DateTime>(Eval(session, "DATE", "past", DateAttr(), ctx));
    }

    [Theory]
    [InlineData("DateOnly")]
    [InlineData("TimeZoneIndependent")]
    [InlineData("UserLocal")]
    public void PastAndBetween_HonorDateBehaviors(string behavior)
    {
        var ctx = Ctx();
        using var session = new BogusEvaluatorSession(ctx.Locale);
        var attr = DateAttr(behavior);

        var past = Assert.IsType<DateTime>(Eval(session, "DATE", "past", attr, ctx));
        AssertKind(behavior, past);

        var betweenRule = new BogusRule("DATE", "between", 1, Args("min", "2020-01-01", "max", "2020-12-31"));
        var prepared = BogusRulePreparer.CompileRule(betweenRule, attr, ctx);
        var between = Assert.IsType<DateTime>(session.Evaluate(prepared, attr, ctx, 0));
        AssertKind(behavior, between);
        Assert.InRange(between.Date, new DateTime(2020, 1, 1), new DateTime(2020, 12, 31));
    }

    [Fact]
    public void SdkMaxCap_ClampsEmittedDateTime()
    {
        var ctx = Ctx();
        var max = DateOnly.FromDateTime(DateTimeAttributeMetadata.MaxSupportedValue);
        var attr = DateAttr("UserLocal");
        var rule = new BogusRule("DATE", "between", 1,
            Args("min", max.ToString("yyyy-MM-dd"), "max", max.ToString("yyyy-MM-dd")));
        var prepared = BogusRulePreparer.CompileRule(rule, attr, ctx);
        using var session = new BogusEvaluatorSession(ctx.Locale);
        var value = Assert.IsType<DateTime>(session.Evaluate(prepared, attr, ctx, 0));
        Assert.True(value <= DateTimeAttributeMetadata.MaxSupportedValue);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Fact]
    public void Utf16AndXml_Rejection()
    {
        Assert.True(BogusEvaluatorSession.IsTransportSafe("Kurtis"));
        Assert.False(BogusEvaluatorSession.IsTransportSafe("\0"));
        Assert.False(BogusEvaluatorSession.IsTransportSafe("\uD800"));
        Assert.False(BogusEvaluatorSession.IsTransportSafe("\uFFFE"));
        Assert.Throws<InvalidOperationException>(() => BogusEvaluatorSession.EnsureTransportSafe("\0"));
    }

    [Fact]
    public void RuleValueGenerator_RejectsRawBogusRule()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleValueGenerator.Evaluate(new BogusRule("NAME", "firstName", 1),
                StringAttr("name"), 42, "account", 0, "r"));
        Assert.Contains("BogusEvaluatorSession", ex.Message, StringComparison.Ordinal);
    }

    private static RuleEvaluationContext Ctx() =>
        new("account", Seed: 42, Locale: "en", RunId: "r", RecordCount: 10);

    private static PreparedBogusRule Compile(
        string api, string endpoint, AttributeMetadata attr, RuleEvaluationContext ctx) =>
        BogusRulePreparer.CompileRule(new BogusRule(api, endpoint, 1), attr, ctx);

    private static object Eval(
        BogusEvaluatorSession session, string api, string endpoint,
        AttributeMetadata attr, RuleEvaluationContext ctx) =>
        session.Evaluate(Compile(api, endpoint, attr, ctx), attr, ctx, 0);

    private static StringAttributeMetadata StringAttr(string name) =>
        new() { LogicalName = name, MaxLength = 200 };

    private static DateTimeAttributeMetadata DateAttr(string? behavior = null)
    {
        var attr = new DateTimeAttributeMetadata { LogicalName = "createdon" };
        if (behavior is not null)
        {
            attr.DateTimeBehavior = behavior switch
            {
                "DateOnly" => DateTimeBehavior.DateOnly,
                "TimeZoneIndependent" => DateTimeBehavior.TimeZoneIndependent,
                _ => DateTimeBehavior.UserLocal,
            };
        }

        return attr;
    }

    private static void AssertKind(string behavior, DateTime value)
    {
        if (behavior is "DateOnly" or "TimeZoneIndependent")
            Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        else
            Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    private static Dictionary<string, JsonElement> Args(params object[] pairs)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
            map[(string)pairs[i]] = JsonSerializer.SerializeToElement(pairs[i + 1]);
        return map;
    }
}
