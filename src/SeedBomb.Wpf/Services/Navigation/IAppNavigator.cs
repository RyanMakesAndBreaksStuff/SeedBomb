namespace SeedBomb.Services.Navigation;

/// <summary>Navigates the shell NavigationView from view models.</summary>
public interface IAppNavigator
{
    /// <summary>Navigates to <paramref name="pageType"/>.</summary>
    void Navigate(Type pageType);
}