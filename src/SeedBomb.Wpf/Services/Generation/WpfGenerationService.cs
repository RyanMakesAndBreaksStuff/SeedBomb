using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using Microsoft.Extensions.Logging;
using Seedbomb.Services.Dataverse;
using System.Diagnostics;

namespace Seedbomb.Services.Generation;

/// <summary>
/// Runs the Bulk/Core generation pipeline from a WPF desktop context.
/// </summary>
public sealed class WpfGenerationService : IWpfGenerationService
{
    private static readonly TimeSpan ProgressGate = TimeSpan.FromMilliseconds(100);

    private readonly IDataverseConnectionService _conn;
    private readonly GenerationPipeline _generationPipeline;
    private readonly ILogger<WpfGenerationService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="conn">Dataverse connection service.</param>
    /// <param name="generationPipeline">Bulk/Core orchestration pipeline.</param>
    /// <param name="logger">Logger.</param>
    public WpfGenerationService(
        IDataverseConnectionService conn,
        GenerationPipeline generationPipeline,
        ILogger<WpfGenerationService> logger)
    {
        _conn = conn;
        _generationPipeline = generationPipeline;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var service = await _conn.GetOrganizationServiceAsync(ct).ConfigureAwait(false);

            var lastAt = TimeSpan.Zero;
            var pipelineProgress = new Progress<GenerationPipelineProgress>(p =>
            {
                var now = sw.Elapsed;
                // Batches complete out of order, so BatchIndex == TotalBatches is not the last
                // snapshot for an entity. Gate on the count so the update that completes a table is
                // never dropped by the throttle.
                // ponytail: lastAt races across pool threads; the worst case is one extra or one
                // skipped throttle tick, which the monotonic guard in AcceptProgress absorbs.
                var isTerminal = p.TotalRecords > 0 && p.RecordsCreated >= p.TotalRecords;
                var isPhaseOnly = string.IsNullOrEmpty(p.EntityLogicalName);
                if (!isTerminal && !isPhaseOnly && now - lastAt < ProgressGate)
                    return;

                lastAt = now;
                progress.Report(new ProgressUpdate(
                    p.Phase,
                    p.EntityLogicalName,
                    p.RecordsCreated,
                    p.TotalRecords,
                    p.BatchIndex,
                    p.TotalBatches,
                    p.RecordsPerMinute,
                    now));
            });

            var result = await _generationPipeline
                .GenerateAsync(config, service, pipelineProgress, ct)
                .ConfigureAwait(false);

            _logger.LogInformation("Generation complete — {Total} records in {Elapsed}",
                result.TotalRecords, sw.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Generation cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation failed");
            throw;
        }
    }
}
