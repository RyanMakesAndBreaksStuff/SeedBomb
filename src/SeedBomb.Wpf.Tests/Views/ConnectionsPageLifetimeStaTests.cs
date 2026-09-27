using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.ViewModels;
using SeedBomb.Views.Pages;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;

namespace SeedBomb.Wpf.Tests.Views;

/// <summary>
/// ConnectionsPage is Transient while ConnectionManagerViewModel is Singleton, so anything the
/// page attaches to the view-model outlives the page unless it is explicitly released.
/// </summary>
[Collection("StaUi")]
public sealed class ConnectionsPageLifetimeStaTests
{
    [StaFact]
    public void UnloadingThePage_ReleasesItsViewModelSubscription()
    {
        EnsureApplication();
        var vm = NewViewModel();
        var before = SubscriberCount(vm);

        // InitializeComponent's DataContext binding also subscribes, so assert on the delta
        // the page's own handler contributes rather than an absolute count.
        var page = new ConnectionsPage(vm);
        var subscribed = SubscriberCount(vm);
        Assert.True(subscribed > before);

        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

        Assert.Equal(subscribed - 1, SubscriberCount(vm));
    }

    [StaFact]
    public async Task NavigatingAway_DropsTheDecryptedSecretFromTheSingleton()
    {
        EnsureApplication();
        var vm = NewViewModel();
        var page = new ConnectionsPage(vm);

        vm.EditingProfile = new ConnectionProfile
        {
            Name = "Dev",
            EnvironmentUrl = "https://org.crm.dynamics.com",
            AuthType = AuthType.ClientSecret,
            ClientSecret = "super-secret-value",
        };

        await page.OnNavigatedFromAsync();

        Assert.Null(vm.EditingProfile);
    }

    [StaFact]
    public async Task NavigatingAway_ClosesTheEditorInsteadOfHalfResettingIt()
    {
        // WR-009: only EditingProfile was cleared, so coming back showed an enabled empty form and
        // a Connect button that did nothing.
        EnsureApplication();
        var vm = NewViewModel();
        var page = new ConnectionsPage(vm);
        vm.EditingProfile = new ConnectionProfile { Name = "Dev", EnvironmentUrl = "https://org.crm.dynamics.com" };
        vm.IsEditing = true;
        vm.IsDirty = true;
        vm.TestResult = "Connected as user@contoso.com";

        await page.OnNavigatedFromAsync();

        Assert.False(vm.IsEditing);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.TestResult);
        Assert.False(vm.ShowConnectButton);
    }

    private static ConnectionManagerViewModel NewViewModel() => new(
        Mock.Of<IConnectionProfileService>(),
        Mock.Of<IAuthService>(),
        Mock.Of<IDataverseConnectionService>());

    // ObservableObject stores PropertyChanged as an ordinary field-like event.
    private static int SubscriberCount(ConnectionManagerViewModel vm)
    {
        var field = typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject)
            .GetField(nameof(INotifyPropertyChanged.PropertyChanged),
                BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (field!.GetValue(vm) as PropertyChangedEventHandler)?.GetInvocationList().Length ?? 0;
    }

    // Mirrors PageViewportStaTests.EnsureApplication; see the note at the end of the plan.
    private static void EnsureApplication()
    {
        if (Application.Current is not null)
            return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/SeedBomb;component/Resources/Shared.xaml", UriKind.Absolute),
        });
    }
}
