using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Abstractions.Controls;

namespace DataGen.Wpf.ViewModels;

/// <summary>
/// Base class for all page view-models. Implements <see cref="INavigationAware"/>
/// so WPF UI can call lifecycle hooks when pages are entered or left.
/// </summary>
public abstract class ViewModelBase : ObservableObject, INavigationAware
{
    /// <inheritdoc />
    public virtual void OnNavigatedTo() { }

    /// <inheritdoc />
    public virtual void OnNavigatedFrom() { }
}
