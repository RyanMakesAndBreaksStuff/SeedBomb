using SeedBomb.Core.Exceptions;

namespace SeedBomb.Core.Contracts;

/// <summary>
/// The one owner of run-size bounds and defaults. Settings, profiles and the UI clamp or reject
/// against these; <see cref="Validate"/> enforces them at the Bulk boundary so no caller bypasses them.
/// </summary>
public static class GenerationLimits
{
    /// <summary>Dataverse caps <c>ExecuteMultiple</c> at 1,000 requests per call.</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>Default rows per batch.</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>Upper bound on one table's record count.</summary>
    public const int MaxRecordCount = 100_000;

    /// <summary>Default rows per table.</summary>
    public const int DefaultRecordCount = 10;

    /// <summary>Upper bound for configured parallelism. Zero (settings) or null (config) means auto.</summary>
    public const int MaxDop = 16;

    /// <summary>Throws when <paramref name="config"/> is outside the bounds. Non-positive counts mean "skip the table" and pass.</summary>
    /// <param name="config">The run to check.</param>
    /// <exception cref="DataGenerationException">A bound is exceeded; the message names it.</exception>
    public static void Validate(GenerationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.BatchSize is < 1 or > MaxBatchSize)
            throw new DataGenerationException(
                $"Batch size must be between 1 and {MaxBatchSize:N0} (was {config.BatchSize:N0}).");
        if (config.MaxParallelism is < 1 or > MaxDop)
            throw new DataGenerationException(
                $"Parallelism must be between 1 and {MaxDop} (was {config.MaxParallelism}).");
        foreach (var (table, count) in config.RecordCounts)
        {
            if (count > MaxRecordCount)
                throw new DataGenerationException(
                    $"Entity '{table}': record count must be at most {MaxRecordCount:N0} (was {count:N0}).");
        }
    }
}
