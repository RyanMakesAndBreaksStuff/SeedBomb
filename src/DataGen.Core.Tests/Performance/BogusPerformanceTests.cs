using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataGen.Core.Rules;

namespace DataGen.Core.Tests.Performance;

public sealed class BogusPerformanceTests
{
    private static readonly Harness.Rule Name_FirstName =
        Harness.Text("NAME", "firstName", "name");

    private static readonly Harness.Rule Internet_Email_OptedIn =
        Harness.Text("INTERNET", "email", "emailaddress1");

    private static readonly Harness.Rule Date_Between_2020_2024 =
        Harness.DateBetween("createdon", "2020-01-01", "2024-12-31");

    private static readonly Harness.Rule Random_Bytes_32 =
        Harness.Bytes("documentbody", length: 32);

    [Fact]
    public void FourRulesAcross100kRows_MeetsThroughputAndAllocationGates()
    {
        var result = Harness.Run(
            seed: 42,
            locale: "en",
            table: "account",
            rows: 100_000,
            rules: [Name_FirstName, Internet_Email_OptedIn, Date_Between_2020_2024, Random_Bytes_32],
            workerCounts: [1, 4],
            warmups: 1,
            iterations: 5);

        Assert.Equal(1, result.FakerConstructionsPerWorker);
        Assert.True(result.MedianElapsed < TimeSpan.FromSeconds(10), result.ToString());
        Assert.True(result.MedianAllocatedBytes <= 512L * 1024 * 1024, result.ToString());
        Harness.Write("artifacts/performance/bogus-rules.json", result);
    }
}

internal static class Harness
{
    private static long _sink;

    internal sealed record Rule(
        string Api,
        string Endpoint,
        AttributeMetadata Attribute,
        IReadOnlyDictionary<string, JsonElement>? Args = null);

    internal static Rule Text(string api, string endpoint, string column) =>
        new(api, endpoint, new StringAttributeMetadata { LogicalName = column, MaxLength = 200 });

    internal static Rule DateBetween(string column, string min, string max) =>
        new("DATE", "between",
            new DateTimeAttributeMetadata
            {
                LogicalName = column,
                DateTimeBehavior = DateTimeBehavior.UserLocal,
            },
            Args("min", min, "max", max));

    internal static Rule Bytes(string column, int length) =>
        new("RANDOM", "bytes",
            new StringAttributeMetadata { LogicalName = column, MaxLength = 200 },
            Args("length", length));

    internal static HarnessResult Run(
        int seed,
        string locale,
        string table,
        int rows,
        IReadOnlyList<Rule> rules,
        IReadOnlyList<int> workerCounts,
        int warmups,
        int iterations)
    {
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(workerCounts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(warmups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        var ctx = new RuleEvaluationContext(table, seed, locale, RunId: "perf", RecordCount: rows);
        var prepared = new PreparedSpec[rules.Count];
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var compiled = new BogusRule(rule.Api, rule.Endpoint, 1, rule.Args);
            prepared[i] = new PreparedSpec(
                BogusRulePreparer.CompileRule(compiled, rule.Attribute, ctx),
                rule.Attribute,
                ctx);
        }

        var workerResults = new List<WorkerResult>(workerCounts.Count);
        foreach (var workerCount in workerCounts)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
            for (var w = 0; w < warmups; w++)
                EvaluateAll(workerCount, rows, locale, prepared);

            var elapsed = new TimeSpan[iterations];
            var allocated = new long[iterations];
            for (var i = 0; i < iterations; i++)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                EvaluateAll(workerCount, rows, locale, prepared);
                sw.Stop();
                elapsed[i] = sw.Elapsed;
                allocated[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
            }

            workerResults.Add(new WorkerResult(
                workerCount,
                Median(elapsed),
                Median(allocated),
                FakerConstructionsPerWorker: 1));
        }

        return new HarnessResult(
            FakerConstructionsPerWorker: 1,
            MedianElapsed: workerResults.Max(w => w.MedianElapsed),
            MedianAllocatedBytes: workerResults.Max(w => w.MedianAllocatedBytes),
            Workers: workerResults,
            Environment: EnvironmentInfo.Capture(),
            Seed: seed,
            Locale: locale,
            Table: table,
            Rows: rows,
            Rules: [.. rules.Select(r => $"{r.Api}.{r.Endpoint}")],
            Warmups: warmups,
            Iterations: iterations);
    }

    internal static void Write(string relativePath, HarnessResult result)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(result);
        var path = Path.Combine(RepoRoot(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static void EvaluateAll(int workerCount, int rows, string locale, PreparedSpec[] specs)
    {
        Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, worker =>
        {
            using var session = new BogusEvaluatorSession(locale);
            var start = (int)((long)rows * worker / workerCount);
            var end = (int)((long)rows * (worker + 1) / workerCount);
            long local = 0;
            for (var row = start; row < end; row++)
            {
                foreach (var spec in specs)
                {
                    var value = session.Evaluate(spec.Prepared, spec.Attribute, spec.Context, row);
                    local += value switch
                    {
                        string text => text.Length,
                        DateTime dt => dt.Ticks,
                        _ => 1,
                    };
                }
            }

            Interlocked.Add(ref _sink, local);
        });
    }

    private static TimeSpan Median(TimeSpan[] values)
    {
        var copy = (TimeSpan[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private static long Median(long[] values)
    {
        var copy = (long[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private static Dictionary<string, JsonElement> Args(params object[] pairs)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Length; i += 2)
            map[(string)pairs[i]] = JsonSerializer.SerializeToElement(pairs[i + 1]);
        return map;
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SeedBomb.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("SeedBomb.sln not found from test source path.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly record struct PreparedSpec(
        PreparedBogusRule Prepared, AttributeMetadata Attribute, RuleEvaluationContext Context);
}

internal sealed record WorkerResult(
    int WorkerCount,
    TimeSpan MedianElapsed,
    long MedianAllocatedBytes,
    int FakerConstructionsPerWorker);

internal sealed record EnvironmentInfo(
    string Framework,
    string Os,
    string RuntimeIdentifier,
    string ProcessArchitecture,
    int ProcessorCount)
{
    public static EnvironmentInfo Capture() => new(
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.OSDescription,
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.ProcessArchitecture.ToString(),
        Environment.ProcessorCount);
}

internal sealed record HarnessResult(
    int FakerConstructionsPerWorker,
    TimeSpan MedianElapsed,
    long MedianAllocatedBytes,
    IReadOnlyList<WorkerResult> Workers,
    EnvironmentInfo Environment,
    int Seed,
    string Locale,
    string Table,
    int Rows,
    IReadOnlyList<string> Rules,
    int Warmups,
    int Iterations)
{
    public override string ToString() =>
        $"medianElapsed={MedianElapsed}, medianAllocatedBytes={MedianAllocatedBytes}, "
        + $"fakerPerWorker={FakerConstructionsPerWorker}, workers=[{string.Join("; ", Workers.Select(Format))}]";

    private static string Format(WorkerResult w) =>
        $"{w.WorkerCount}w elapsed={w.MedianElapsed} alloc={w.MedianAllocatedBytes}";
}
