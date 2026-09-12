using System.Diagnostics;
using DataGen.Bulk;
using DataGen.Bulk.Contracts;
using DataGen.Core.Contracts;
using Microsoft.Extensions.Logging;
using Seedbomb.Services.Dataverse;

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
                var isTerminal = p.BatchIndex == p.TotalBatches && p.TotalBatches > 0;
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
