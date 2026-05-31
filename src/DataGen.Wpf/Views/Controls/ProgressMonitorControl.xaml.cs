using System.Windows;
using System.Windows.Controls;
using DataGen.Wpf.Services.Generation;

namespace DataGen.Wpf.Views.Controls;

/// <summary>
/// Displays a generation progress snapshot. Bind the <see cref="Progress"/> dependency
/// property to a <see cref="ProgressUpdate"/> observable property on the parent ViewModel.
/// </summary>
public partial class ProgressMonitorControl : UserControl
{
    /// <summary>The current progress snapshot to display.</summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress),
            typeof(ProgressUpdate),
            typeof(ProgressMonitorControl),
            new PropertyMetadata(null, OnProgressChanged));

    /// <summary>Gets or sets the current progress snapshot.</summary>
    public ProgressUpdate? Progress
    {
        get => (ProgressUpdate?)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>Initialises the control.</summary>
    public ProgressMonitorControl() => InitializeComponent();

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProgressMonitorControl ctrl)
            ctrl.Refresh(e.NewValue as ProgressUpdate);
    }

    private void Refresh(ProgressUpdate? p)
    {
        if (p is null)
        {
            PhaseText.Text = string.Empty;
            EntityText.Text = string.Empty;
            ProgressBar.Value = 0;
            ProgressBar.IsIndeterminate = false;
            RecordsSummary.Text = string.Empty;
            RecordsPerMin.Text = string.Empty;
            ElapsedText.Text = string.Empty;
            BatchSummary.Text = string.Empty;
            return;
        }

        PhaseText.Text = p.Phase;
        EntityText.Text = p.EntityName;

        if (p.TotalRecords > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = p.TotalRecords;
            ProgressBar.Value = p.RecordsCreated;
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
        }

        RecordsSummary.Text = $"{p.RecordsCreated:N0} / {p.TotalRecords:N0}";
        RecordsPerMin.Text = p.RecordsPerMinute > 0 ? $"{p.RecordsPerMinute:N0} rec/min" : string.Empty;
        ElapsedText.Text = $"{(int)p.Elapsed.TotalMinutes:00}:{p.Elapsed.Seconds:00}";
        BatchSummary.Text = p.TotalBatches > 0
            ? $"Batch {p.BatchesCompleted} / {p.TotalBatches}"
            : string.Empty;
    }
}
