namespace SeedBomb.ViewModels;

/// <summary>
/// WR-001: keeps the main window open while a Generate or Retry run, including Generate's History
/// write, is in flight, and cancels it only after the user confirms.
/// </summary>
/// <param name="generate">The Generate wizard; its <see cref="GenerateViewModel.Run"/> owns Retry.</param>
/// <param name="confirm">Asks whether to cancel the run and close.</param>
/// <param name="close">Closes the window again once the cancelled run has finished.</param>
internal sealed class RunCloseGuard(GenerateViewModel generate, Func<Task<bool>> confirm, Action close)
{
    private bool _busy;
    private bool _released;

    /// <summary>
    /// Returns <see langword="true"/> when the window may close now. Otherwise the caller cancels
    /// this close, and <c>close</c> runs once the run and its History row are done.
    /// </summary>
    public bool AllowClose()
    {
        if (_released)
            return true;
        if (_busy)
            return false; // repeated request while the confirm is open or the run is finishing
        if (InFlight().Length == 0)
            return true;

        _busy = true;
        _ = ConfirmAndCloseAsync();
        return false;
    }

    // The command tasks span preparation, the write, the outcome and Generate's History row.
    // Run.IsRunning covers only the write, and Retry never goes through GenerateAsync.
    private Task[] InFlight() =>
        new[] { generate.GenerateCommand.ExecutionTask, generate.Run.RetrySelectedCommand.ExecutionTask }
            .OfType<Task>()
            .Where(t => !t.IsCompleted)
            .ToArray();

    private async Task ConfirmAndCloseAsync()
    {
        bool confirmed;
        try
        {
            confirmed = await confirm();
        }
        catch (Exception)
        {
            confirmed = false; // no dialog host: stay open rather than cancel the run unasked
        }

        if (!confirmed)
        {
            _busy = false;
            return;
        }

        generate.CancelForClose();
        generate.Run.CancelForClose();
        try
        {
            await Task.WhenAll(InFlight());
        }
        catch (Exception)
        {
            // Both commands report their own failures; the close still goes ahead.
        }

        _released = true;
        close();
    }
}
