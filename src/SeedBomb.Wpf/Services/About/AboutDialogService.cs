using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace SeedBomb.Services.About;

/// <summary>Displays bundled license text in the application's dialog host.</summary>
public sealed class AboutDialogService(
    IThirdPartyNoticeService notices,
    IContentDialogService dialogs) : IAboutDialogService
{
    /// <inheritdoc />
    public Task ShowAppLicenseAsync(string version) =>
        ShowAsync($"{AppInfo.ProductName} {version} — License", notices.ReadAppLicense());

    /// <inheritdoc />
    public Task ShowLicenseAsync(ThirdPartyComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return ShowAsync($"{component.Name} {component.Version} — {component.License}",
            notices.ReadLicense(component));
    }

    private async Task ShowAsync(string title, string license)
    {
        var text = new TextBox
        {
            Text = license,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 360,
            MinWidth = 280,
            MaxWidth = 640,
        };
        text.SetResourceReference(FrameworkElement.StyleProperty, "DG.TextBox");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = text,
            CloseButtonText = "Close",
        };
        await dialogs.ShowAsync(dialog, CancellationToken.None);
    }
}
