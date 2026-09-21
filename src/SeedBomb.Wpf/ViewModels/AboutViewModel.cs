using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Seedbomb.Services.About;
using System.Collections.ObjectModel;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.ViewModels;

/// <summary>ViewModel for the About page.</summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    private readonly IThirdPartyNoticeService _notices;
    private readonly IUriLauncher _uriLauncher;
    private readonly IAboutDialogService _dialogs;
    private readonly ISnackbarService? _snackbar;

    public AboutViewModel(
        IThirdPartyNoticeService notices,
        IUriLauncher uriLauncher,
        IAboutDialogService dialogs,
        ISnackbarService? snackbar = null)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(uriLauncher);
        ArgumentNullException.ThrowIfNull(dialogs);
        _notices = notices;
        _uriLauncher = uriLauncher;
        _dialogs = dialogs;
        _snackbar = snackbar;

        ProductName = AppInfo.ProductName;
        Description = AppInfo.Description;
        Author = AppInfo.Author;
        Version = AppInfo.ReadVersion(typeof(Seedbomb.App).Assembly);
        Runtime = AppInfo.ReadRuntime();
        SourceCodeUrl = AppInfo.SourceCodeUrl;
        DocumentationUrl = AppInfo.DocumentationUrl;
        IssuesUrl = AppInfo.IssuesUrl;

        var loaded = _notices.Load();
        NoticeError = loaded.Errors.Count == 0
            ? ""
            : "Some third-party notices could not be loaded.";
        foreach (var component in _notices.GetFeatured(loaded))
            Featured.Add(component);
    }

    public string ProductName { get; }
    public string Description { get; }
    public string Author { get; }
    public string Version { get; }
    public string Runtime { get; }
    public string SourceCodeUrl { get; }
    public string DocumentationUrl { get; }
    public string IssuesUrl { get; }
    public string NoticeError { get; }
    public bool HasNoticeError => NoticeError.Length > 0;
    public ObservableCollection<ThirdPartyComponent> Featured { get; } = [];

    [RelayCommand(CanExecute = nameof(CanOpenUrl))]
    private void OpenUrl(string? url)
    {
        if (!_uriLauncher.TryOpen(url, out var error))
        {
            _snackbar?.Show("Could not open link", error,
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(6));
        }
    }

    private bool CanOpenUrl(string? url) => _uriLauncher.CanOpen(url);

    [RelayCommand]
    private async Task ShowAppLicenseAsync()
    {
        await _dialogs.ShowAppLicenseAsync(Version);
    }

    [RelayCommand]
    private async Task ShowComponentLicenseAsync(ThirdPartyComponent? component)
    {
        if (component is null) return;
        await _dialogs.ShowLicenseAsync(component);
    }
}
