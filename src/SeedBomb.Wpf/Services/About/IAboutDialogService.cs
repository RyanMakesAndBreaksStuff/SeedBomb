namespace SeedBomb.Services.About;

/// <summary>Shows license and related About dialogs.</summary>
public interface IAboutDialogService
{
    /// <summary>Shows the SeedBomb license.</summary>
    Task ShowAppLicenseAsync(string version);

    /// <summary>Shows <paramref name="component"/>'s license text.</summary>
    Task ShowLicenseAsync(ThirdPartyComponent component);
}