namespace Seedbomb.Services.About;

/// <summary>Shows license and related About dialogs.</summary>
public interface IAboutDialogService
{
    /// <summary>Shows the DataGen/SeedBomb license.</summary>
    Task ShowAppLicenseAsync(string version);

    /// <summary>Shows <paramref name="component"/>'s license text.</summary>
    Task ShowLicenseAsync(ThirdPartyComponent component);
}