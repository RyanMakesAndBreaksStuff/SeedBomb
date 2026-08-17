using System.Diagnostics;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests.Performance;

public sealed class RuleEditorPreviewPerformanceTests
{
    [Fact]
    public void CatchPhraseThreeValuePreview_P95Under50ms()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 200 };
        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, new AttributeMetadata[] { attr });
        meta.GetType().GetProperty("Keys")!.SetValue(meta, Array.Empty<EntityKeyMetadata>());

        var vm = new RuleEditorViewModel(meta, recordCount: 10, seed: 42, runId: "perf");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == "name");
        vm.ApplyExistingRule(new BogusRule("COMPANY", "catchPhrase", 1));
        Assert.True(vm.CanSave);
        Assert.Equal("bogus", vm.SelectedOp);
        Assert.Equal("COMPANY", vm.SelectedBogusApi);
        Assert.Equal("COMPANY.catchPhrase", vm.SelectedBogusEndpoint);

        var rule = new BogusRule("COMPANY", "catchPhrase", 1);
        var ctx = new RuleEvaluationContext("account", Seed: 42, Locale: "en", RunId: "perf", RecordCount: 10);

        for (var i = 0; i < 10; i++)
            PreviewThree(rule, attr, ctx);

        var samples = new double[100];
        var sw = new Stopwatch();
        for (var i = 0; i < samples.Length; i++)
        {
            sw.Restart();
            PreviewThree(rule, attr, ctx);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var p95 = samples[(int)Math.Ceiling(0.95 * samples.Length) - 1];
        Assert.True(p95 < 50,
            $"COMPANY.catchPhrase three-value preview p95={p95:F3} ms (gate 50 ms). "
            + $"min={samples[0]:F3} median={samples[samples.Length / 2]:F3} max={samples[^1]:F3}.");
    }

    private static void PreviewThree(BogusRule rule, AttributeMetadata attr, RuleEvaluationContext ctx)
    {
        var prepared = BogusRulePreparer.CompileRule(rule, attr, ctx);
        using var session = new BogusEvaluatorSession(ctx.Locale);
        for (var row = 0; row < 3; row++)
            _ = session.Evaluate(prepared, attr, ctx, row);
    }
}
