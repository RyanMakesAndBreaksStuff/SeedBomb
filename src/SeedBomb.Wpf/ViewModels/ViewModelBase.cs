using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Abstractions.Controls;

namespace Seedbomb.ViewModels;

/// <summary>
/// Base class for all page view-models. Implements <see cref="INavigationAware"/>
/// so WPF UI can call lifecycle hooks when pages are entered or left.
/// </summary>
public abstract class ViewModelBase : ObservableObject, INavigationAware
{
    /// <inheritdoc />
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}