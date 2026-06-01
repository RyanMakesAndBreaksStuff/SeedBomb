using DataGen.Core.Contracts;

namespace DataGen.Wpf.Services.Generation;

/// <summary>
/// Runs the Core/Bulk generation pipeline from a WPF context.
/// </summary>
public interface IWpfGenerationService
{
    /// <summary>
    /// Executes a generation run, dispatching <see cref="ProgressUpdate"/> snapshots
    /// to the UI thread via <paramref name="progress"/>.
    /// </summary>
    Task<GenerationResult> GenerateAsync(
        GenerationConfig config,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default);
}
