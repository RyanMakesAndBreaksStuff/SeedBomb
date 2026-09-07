using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.ViewModels;
using Seedbomb.Views.Behaviors;
using Seedbomb.Views.Pages;
using Wpf.Ui.Appearance;
using CalendarDatePicker = Wpf.Ui.Controls.CalendarDatePicker;
using Wpf.Ui.Markup;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace DataGen.Wpf.Tests.Views;

public sealed class RulesPageStaTests : IDisposable
{
    private static readonly List<string> CapturedBindingErrors = [];
    private readonly List<Window> _windows = [];

    [StaFact]
    public void EndpointComboBox_SelectingOption_PushesEndpointIdToViewModel()
    {
        var (page, vm) = LoadRulesPageOnSta(StringColumn());
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "NAME";
        Flush();

        var combo = (ComboBox)page.FindName("BogusEndpointComboBox")!;
        combo.SelectedItem = vm.BogusEndpoints.Single(o => o.Id == "NAME.firstName");
        Flush();

        Assert.Equal("NAME.firstName", vm.SelectedBogusEndpoint);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void NumericLengthAndDateControls_PushValuesToViewModel()
    {
        var (numericPage, numericVm) = LoadRulesPageOnSta(IntegerColumn());
        numericVm.SelectedOp = "bogus";
        numericVm.SelectedBogusApi = "RANDOM";
        Flush();
        SelectEndpoint(numericPage, numericVm, "RANDOM.number");

        var minBox = (TextBox)numericPage.FindName("BogusMinNumberTextBox")!;
        var maxBox = (TextBox)numericPage.FindName("BogusMaxNumberTextBox")!;
        Assert.True(minBox.IsVisible);
        minBox.Text = "3";
        maxBox.Text = "9";
        Flush();
        Assert.Equal("3", numericVm.BogusMinNumber);
        Assert.Equal("9", numericVm.BogusMaxNumber);
        Assert.True(numericVm.CanSave);
        Assert.Empty(CapturedBindingErrors);

        var (lengthPage, lengthVm) = LoadRulesPageOnSta(StringColumn());
        lengthVm.SelectedOp = "bogus";
        lengthVm.SelectedBogusApi = "RANDOM";
        Flush();
        SelectEndpoint(lengthPage, lengthVm, "RANDOM.digits");
        var lengthBox = (TextBox)lengthPage.FindName("BogusLengthTextBox")!;
        Assert.True(lengthBox.IsVisible);
        lengthBox.Text = "8";
        Flush();
        Assert.Equal("8", lengthVm.BogusLengthText);
        Assert.True(lengthVm.CanSave);

        var (datePage, dateVm) = LoadRulesPageOnSta(DateColumn());
        dateVm.SelectedOp = "bogus";
        dateVm.SelectedBogusApi = "DATE";
        Flush();
        SelectEndpoint(datePage, dateVm, "DATE.between");
        var minDate = (CalendarDatePicker)datePage.FindName("BogusMinDatePicker")!;
        var maxDate = (CalendarDatePicker)datePage.FindName("BogusMaxDatePicker")!;
        Assert.True(minDate.IsVisible);
        minDate.Date = new DateTime(2020, 1, 1);
        maxDate.Date = new DateTime(2020, 12, 31);
        Flush();
        Assert.Equal(new DateTime(2020, 1, 1), dateVm.BogusMinDate);
        Assert.Equal(new DateTime(2020, 12, 31), dateVm.BogusMaxDate);
        Assert.True(dateVm.CanSave);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void RestoreExistingRule_SelectsEndpointAndKeepsSaveEnabled()
    {
        var (page, vm) = LoadRulesPageOnSta(StringColumn());
        vm.ApplyExistingRule(new BogusRule("NAME", "firstName", 1));
        Flush();

        var combo = (ComboBox)page.FindName("BogusEndpointComboBox")!;
        Assert.Equal("NAME.firstName", vm.SelectedBogusEndpoint);
        Assert.Equal("NAME.firstName", combo.SelectedValue);
        Assert.True(vm.CanSave);
        Assert.Empty(vm.GetErrors(nameof(vm.SelectedBogusEndpoint)).Cast<object>());
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void SchemaSwitch_ClearsHiddenArgumentValues()
    {
        var (page, vm) = LoadRulesPageOnSta(StringColumn());
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "RANDOM";
        Flush();
        SelectEndpoint(page, vm, "RANDOM.digits");
        var lengthBox = (TextBox)page.FindName("BogusLengthTextBox")!;
        lengthBox.Text = "8";
        Flush();
        Assert.Equal("8", vm.BogusLengthText);

        vm.SelectedBogusApi = "NAME";
        Flush();
        SelectEndpoint(page, vm, "NAME.firstName");

        Assert.Equal("", vm.BogusLengthText);
        Assert.False(lengthBox.IsVisible);
        Assert.True(vm.CanSave);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void FocusFirstError_FocusesVisibleTarget_AndSkipsCollapsed()
    {
        var (page, vm) = LoadRulesPageOnSta(StringColumn());
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "NAME";
        Flush();

        var endpoint = (ComboBox)page.FindName("BogusEndpointComboBox")!;
        var lengthBox = (TextBox)page.FindName("BogusLengthTextBox")!;
        Assert.False(lengthBox.IsVisible);
        Assert.False(ValidationFocusBehavior.FocusFirst(page, RuleInputTarget.Length));
        Assert.False(lengthBox.IsKeyboardFocusWithin);

        Assert.True(ValidationFocusBehavior.FocusFirstError(page, vm.Messages));
        Assert.True(endpoint.IsKeyboardFocused || endpoint.IsFocused || endpoint.IsKeyboardFocusWithin);
        Assert.Empty(CapturedBindingErrors);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
    }

    private (RulesPage page, RuleEditorViewModel vm) LoadRulesPageOnSta(AttributeMetadata column)
    {
        EnsureApplication();
        StartBindingTrace();
        CapturedBindingErrors.Clear();

        var vm = new RuleEditorViewModel(BuildEntity(column), recordCount: 10, seed: 42, runId: "r1");
        vm.SelectedColumn = vm.SettableColumns.Single(c => c.LogicalName == column.LogicalName);

        var page = new RulesPage(vm);
        var window = new Window
        {
            Content = page,
            Width = 1280,
            Height = 800,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        _windows.Add(window);
        window.Show();
        page.UpdateLayout();
        window.UpdateLayout();
        Flush();
        return (page, vm);
    }

    private static void SelectEndpoint(RulesPage page, RuleEditorViewModel vm, string id)
    {
        var combo = (ComboBox)page.FindName("BogusEndpointComboBox")!;
        combo.SelectedItem = vm.BogusEndpoints.Single(o => o.Id == id);
        Flush();
    }

    private static StringAttributeMetadata StringColumn() =>
        new() { LogicalName = "name", IsValidForCreate = true, MaxLength = 20 };

    private static IntegerAttributeMetadata IntegerColumn() =>
        new() { LogicalName = "numberofemployees", IsValidForCreate = true, MinValue = 0, MaxValue = 1_000_000 };

    private static DateTimeAttributeMetadata DateColumn() =>
        new() { LogicalName = "scheduledon", IsValidForCreate = true };

    private static EntityMetadata BuildEntity(AttributeMetadata column)
    {
        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 20 };
        var employees = new IntegerAttributeMetadata
        {
            LogicalName = "numberofemployees",
            IsValidForCreate = true,
            MinValue = 0,
            MaxValue = 1_000_000,
        };
        var scheduled = new DateTimeAttributeMetadata { LogicalName = "scheduledon", IsValidForCreate = true };
        var extras = new AttributeMetadata[] { name, employees, scheduled };
        if (extras.All(a => a.LogicalName != column.LogicalName))
            extras = [.. extras, column];

        var meta = new EntityMetadata { LogicalName = "account" };
        meta.GetType().GetProperty("Attributes")!.SetValue(meta, extras);
        return meta;
    }

    private static void Flush() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

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

    private static void StartBindingTrace()
    {
        if (PresentationTraceSources.DataBindingSource.Listeners.OfType<BindingErrorListener>().Any())
            return;

        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                CapturedBindingErrors.Add(message);
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer(typeof(StaFactDiscoverer))]
internal sealed class StaFactAttribute : FactAttribute
{
    public StaFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
    }
}

internal sealed class StaFactDiscoverer : FactDiscoverer
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var inner = (XunitTestCase)base.CreateTestCase(discoveryOptions, testMethod, factAttribute);
        return new StaXunitTestCase(inner);
    }
}

internal sealed class StaXunitTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
#pragma warning disable CS0618
    public StaXunitTestCase()
    {
    }
#pragma warning restore CS0618

    public StaXunitTestCase(XunitTestCase inner)
        : base(
            inner.TestMethod,
            inner.TestCaseDisplayName,
            inner.UniqueID,
            inner.Explicit,
            inner.SkipExceptions,
            inner.SkipReason,
            inner.SkipType,
            inner.SkipUnless,
            inner.SkipWhen,
            inner.Traits,
            inner.TestMethodArguments,
            inner.SourceFilePath,
            inner.SourceLineNumber,
            inner.Timeout)
    {
    }

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager methodFixtureMappings)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return RunCore(explicitOption, messageBus, constructorArguments, aggregator, cancellationTokenSource, parallelMode, scheduler, methodFixtureMappings);

        var tcs = new TaskCompletionSource<RunSummary>();
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(RunCore(
                        explicitOption,
                        messageBus,
                        constructorArguments,
                        aggregator,
                        cancellationTokenSource,
                        parallelMode,
                        scheduler,
                        methodFixtureMappings)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return new ValueTask<RunSummary>(tcs.Task);
    }

    private async ValueTask<RunSummary> RunCore(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager methodFixtureMappings)
    {
        var tests = await CreateTests();

        return await XunitTestCaseRunner.Instance.Run(
            this,
            tests,
            messageBus,
            aggregator,
            cancellationTokenSource,
            parallelMode,
            scheduler,
            TestCaseDisplayName,
            SkipReason,
            explicitOption,
            constructorArguments,
            methodFixtureMappings);
    }
}
