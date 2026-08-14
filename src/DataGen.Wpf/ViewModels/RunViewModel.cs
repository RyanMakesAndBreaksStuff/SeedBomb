using CommunityToolkit.Mvvm.ComponentModel;

namespace Seedbomb.ViewModels;

/// <summary>Drives the in-progress sheet and the run summary. Filled in Task 9.</summary>
public sealed partial class RunViewModel : ObservableObject
{
    [ObservableProperty] private bool _isRunning;
}
