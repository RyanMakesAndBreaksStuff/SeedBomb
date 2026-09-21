using Wpf.Ui.Controls;

namespace SeedBomb.Services.Navigation;

/// <summary>Forwards navigation to the live <see cref="NavigationView"/>.</summary>
public sealed class NavigationViewNavigator : IAppNavigator
{
    /// <summary>Set from <c>MainWindow</c> on Loaded.</summary>
    public NavigationView? Control { get; set; }

    /// <inheritdoc />
    public void Navigate(Type pageType) => Control?.Navigate(pageType);
}