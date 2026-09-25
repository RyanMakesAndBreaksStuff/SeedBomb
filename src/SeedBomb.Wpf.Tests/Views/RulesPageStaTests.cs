using SeedBomb.Core.Metadata;
using SeedBomb.Core.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Auth;
using SeedBomb.Services.Connections;
using SeedBomb.Services.Dataverse;
using SeedBomb.Services.Navigation;
using SeedBomb.Services.Profiles;
using SeedBomb.ViewModels;
using SeedBomb.ViewModels.Controls;
using SeedBomb.Views.Controls;
using SeedBomb.Views.Pages;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogButton = Wpf.Ui.Controls.ContentDialogButton;
using ContentDialogHost = Wpf.Ui.Controls.ContentDialogHost;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SeedBomb.Wpf.Tests.Views;

[Collection("StaUi")]
public sealed class RulesPageStaTests : IDisposable
{
    private static readonly List<string> CapturedBindingErrors = [];
    private readonly List<Window> _windows = [];

    [StaFact]
    public void EndpointComboBox_SelectingOption_PushesEndpointIdToViewModel()
    {
        var (_, vm) = LoadRulesPageOnSta(StringColumn());
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "NAME";
        Flush();

        vm.SelectedBogusEndpoint = "NAME.firstName";
        Flush();

        Assert.Equal("NAME.firstName", vm.SelectedBogusEndpoint);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void NumericLengthAndDateControls_PushValuesToViewModel()
    {
        var (_, numericVm) = LoadRulesPageOnSta(IntegerColumn());
        numericVm.SelectedOp = "bogus";
        numericVm.SelectedBogusApi = "RANDOM";
        Flush();
        SelectEndpoint(numericVm, "RANDOM.number");

        Assert.True(numericVm.BogusInput.BogusHasNumericArgs);
        numericVm.BogusMinNumber = "3";
        numericVm.BogusMaxNumber = "9";
        Flush();
        Assert.Equal("3", numericVm.BogusMinNumber);
        Assert.Equal("9", numericVm.BogusMaxNumber);
        Assert.True(numericVm.CanSave);
        Assert.Empty(CapturedBindingErrors);

        var (_, lengthVm) = LoadRulesPageOnSta(StringColumn());
        lengthVm.SelectedOp = "bogus";
        lengthVm.SelectedBogusApi = "RANDOM";
        Flush();
        SelectEndpoint(lengthVm, "RANDOM.digits");
        Assert.True(lengthVm.BogusInput.BogusHasLengthArg);
        lengthVm.BogusLengthText = "8";
        Flush();
        Assert.Equal("8", lengthVm.BogusLengthText);
        Assert.True(lengthVm.CanSave);

        var (_, dateVm) = LoadRulesPageOnSta(DateColumn());
        dateVm.SelectedOp = "bogus";
        dateVm.SelectedBogusApi = "DATE";
        Flush();
        SelectEndpoint(dateVm, "DATE.between");
        Assert.True(dateVm.BogusInput.BogusHasDateArgs);
        dateVm.BogusMinDate = new DateTime(2020, 1, 1);
        dateVm.BogusMaxDate = new DateTime(2020, 12, 31);
        Flush();
        Assert.Equal(new DateTime(2020, 1, 1), dateVm.BogusMinDate);
        Assert.Equal(new DateTime(2020, 12, 31), dateVm.BogusMaxDate);
        Assert.True(dateVm.CanSave);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void RestoreExistingRule_SelectsEndpointAndKeepsSaveEnabled()
    {
        var (_, vm) = LoadRulesPageOnSta(StringColumn());
        vm.ApplyExistingRule(new BogusRule("NAME", "firstName", 1));
        Flush();

        Assert.Equal("NAME.firstName", vm.SelectedBogusEndpoint);
        Assert.True(vm.CanSave);
        Assert.Empty(vm.GetErrors(nameof(vm.SelectedBogusEndpoint)).Cast<object>());
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void SchemaSwitch_ClearsHiddenArgumentValues()
    {
        var (_, vm) = LoadRulesPageOnSta(StringColumn());
        vm.SelectedOp = "bogus";
        vm.SelectedBogusApi = "RANDOM";
        Flush();
        SelectEndpoint(vm, "RANDOM.digits");
        vm.BogusLengthText = "8";
        Flush();
        Assert.Equal("8", vm.BogusLengthText);

        vm.SelectedBogusApi = "NAME";
        Flush();
        SelectEndpoint(vm, "NAME.firstName");

        Assert.Equal("", vm.BogusLengthText);
        Assert.False(vm.BogusInput.BogusHasLengthArg);
        Assert.True(vm.CanSave);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void OperationComboBox_BindsAvailableOpOptions_WithoutSplitOneOfIds()
    {
        var lookup = new LookupAttributeMetadata
        {
            LogicalName = "parentaccountid",
            IsValidForCreate = true,
            Targets = ["account"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(lookup, AttributeTypeCode.Lookup);
        var (page, vm) = LoadRulesPageOnSta(lookup);
        Flush();

        var combo = FindVisualChildren<ComboBox>(page)
            .Single(c => AutomationProperties.GetName(c) == "Operation");
        Assert.Equal(vm.AvailableOpOptions, combo.ItemsSource);
        Assert.Equal(["constant", "oneOf", "lookupRandom", "null"], vm.AvailableOps);
        Assert.DoesNotContain(vm.AvailableOpOptions, o => o.Op is "oneOfManual" or "oneOfPicker");
        Assert.Contains(vm.AvailableOpOptions, o => o.Op == "oneOf" && o.Title == "one-of");
        Assert.Contains(vm.AvailableOpOptions, o => o.Op == "lookupRandom" && o.Title == "random (existing records)");
        Assert.Equal("constant", combo.SelectedValue);

        vm.SelectedOp = "lookupRandom";
        Flush();
        Assert.Equal("lookupRandom", combo.SelectedValue);
        Assert.Equal("lookupRandom", vm.SelectedOp);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void OperationPane_SwapsTemplateForEveryOperationKind()
    {
        var (page, vm) = LoadRulesPageOnSta(StringColumn());
        var content = FindVisualChildren<ContentControl>(page)
            .Single(c => ReferenceEquals(c.Content, vm));

        var templates = new HashSet<DataTemplate>();
        foreach (var op in new[] { "constant", "range", "sequence", "pattern", "oneOf", "bogus", "lookupRandom" })
        {
            vm.SelectedOp = op;
            Flush();
            Assert.NotNull(content.ContentTemplate);
            templates.Add(content.ContentTemplate);
        }

        Assert.Equal(7, templates.Count);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void OperationTemplateHost_DoesNotAdornRuleLevelErrors()
    {
        var (page, vm) = LoadRulesPageOnSta(StringColumn());
        var host = FindVisualChildren<ContentControl>(page).Single(c => ReferenceEquals(c.Content, vm));

        foreach (var op in new[] { "pattern", "range", "bogus" })
        {
            vm.SelectedOp = op;
            Flush();
            Assert.True(vm.HasMessages, $"{op} should carry a rule message for the InfoBar.");
            Assert.False(Validation.GetHasError(host), $"{op}: template host shows the red validation outline.");
        }
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void ColumnGroups_CollapseOnHeaderClick_AndStayCollapsedAcrossRefresh()
    {
        var (page, vm) = LoadRulesPageForProfileOnSta();
        var header = GroupHeader(page, "Disabled");
        Assert.True(header.IsChecked);
        Assert.Contains(FindVisualChildren<ListBoxItem>(FindAncestor<GroupItem>(header)), i => i.IsVisible);

        InvokeClick(header);
        Flush();
        Assert.False(header.IsChecked);
        Assert.DoesNotContain(FindVisualChildren<ListBoxItem>(FindAncestor<GroupItem>(header)), i => i.IsVisible);

        // Every rule save refreshes the view, which regenerates the group containers.
        vm.ColumnsView!.Refresh();
        Flush();
        Assert.False(GroupHeader(page, "Disabled").IsChecked);
        Assert.True(GroupHeader(page, "Unmapped").IsChecked);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void ConnectionsTestResultBanner_SwapsBrushGlyphAndVisibility()
    {
        var (page, vm) = LoadConnectionsPageOnSta();

        // Untested state: no TestResult → banner collapsed.
        var marker = "banner-sta-" + Guid.NewGuid().ToString("N");
        vm.TestResult = marker;
        Flush();
        var text = FindVisualChildren<TextBlock>(page).Single(t => t.Text == marker);
        var banner = FindAncestor<Border>(text);
        var glyph = FindVisualChildren<SymbolIcon>(banner).Single();

        // Failed test → warning variant.
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Assert.Equal(ExpectedColor("DG.WarningSoft"), ((SolidColorBrush)banner.Background).Color);
        Assert.Equal(ExpectedColor("DG.WarningBorder"), ((SolidColorBrush)banner.BorderBrush).Color);
        Assert.Equal(SymbolRegular.Warning24, glyph.Symbol);
        Assert.Equal(ExpectedColor("DG.Warning"), ((SolidColorBrush)glyph.Foreground).Color);

        // Successful test → accent/success variant.
        vm.TestSucceeded = true;
        Flush();
        Assert.Equal(ExpectedColor("DG.AccentSoft"), ((SolidColorBrush)banner.Background).Color);
        Assert.Equal(ExpectedColor("DG.AccentSoftBorder"), ((SolidColorBrush)banner.BorderBrush).Color);
        Assert.Equal(SymbolRegular.CheckmarkCircle24, glyph.Symbol);
        Assert.Equal(ExpectedColor("DG.Success"), ((SolidColorBrush)glyph.Foreground).Color);

        // Cleared result → untested state collapses the banner again.
        vm.TestResult = null;
        Flush();
        Assert.Equal(Visibility.Collapsed, banner.Visibility);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void LookupPicker_Buttons_BindToViewModelCommands()
    {
        var source = new ScriptedLookupSource();
        using var vm = new LookupRecordPickerViewModel(source, NullLogger<LookupRecordPickerViewModel>.Instance);
        var selected = new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme");
        vm.Initialize(new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] },
            [selected], single: true, CancellationToken.None);
        var picker = LoadPicker(vm);

        var search = FindButtons(picker).Single(b => Equals(b.Content, "Search"));
        var addHighlighted = FindButtons(picker).Single(b => Equals(b.Content, "Select highlighted record"));
        var next = FindButtons(picker).Single(b => Equals(b.Content, "Next page"));
        var remove = FindButtons(picker).Single(b => Equals(b.Content, "Remove"));
        Assert.Same(vm.SearchCommand, search.Command);
        Assert.Same(vm.AddHighlightedCommand, addHighlighted.Command);
        Assert.Same(vm.NextPageCommand, next.Command);
        Assert.Same(vm.RemoveCommand, remove.Command);
        Assert.Same(vm.Selected[0], remove.CommandParameter);
        Assert.False(next.IsEnabled);

        var enter = picker.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.Enter);
        Assert.Same(vm.SearchCommand, enter.Command);

        var searchBox = FindVisualChildren<TextBox>(picker).Single(t => t.MaxLength == 200);
        searchBox.Focus();
        Flush();
        Assert.True(searchBox.IsKeyboardFocused || searchBox.IsFocused || searchBox.IsKeyboardFocusWithin);

        InvokeClick(search);
        Assert.Single(source.Requests);
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void LookupPicker_Results_VirtualizeToFiniteViewport()
    {
        using var vm = new LookupRecordPickerViewModel(new ScriptedLookupSource(),
            NullLogger<LookupRecordPickerViewModel>.Instance);
        vm.Initialize(new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] },
            [], single: true, CancellationToken.None);
        var picker = LoadPicker(vm);
        for (var i = 0; i < 80; i++)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}");
            vm.Results.Add(new LookupRecord(new LookupRuleValue("account", id, $"Row {i}"), "—", "—"));
        }
        picker.UpdateLayout();
        Flush();
        Flush();

        var list = FindVisualChildren<ListView>(picker).Single();
        list.UpdateLayout();
        Flush();
        Assert.True(list.ActualHeight > 40);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
        Assert.Equal(80, list.Items.Count);
        var generated = CountGeneratedContainers(list);
        Assert.True(generated > 0, "Expected at least one generated row container.");
        Assert.True(generated < list.Items.Count,
            $"Expected a finite viewport, generated {generated} of {list.Items.Count}.");
        Assert.Empty(CapturedBindingErrors);
    }

    [StaFact]
    public void LookupPicker_DialogHost_EnterSearch_EscapeCancel_AndPrimaryAdd()
    {
        EnsureApplication();
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        StartBindingTrace();
        CapturedBindingErrors.Clear();

        var host = new ContentDialogHost();
        var window = new Window
        {
            Content = host,
            Width = 900,
            Height = 700,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        _windows.Add(window);
        window.Show();
        window.UpdateLayout();
        Flush();

        var dialogs = new ContentDialogService();
        dialogs.SetDialogHost(host);
        var source = new ScriptedLookupSource();
        var service = new LookupRecordPickerService(dialogs,
            () => new LookupRecordPickerViewModel(source, NullLogger<LookupRecordPickerViewModel>.Instance));
        var original = new[]
        {
            new LookupRuleValue("account", Guid.Parse("11111111-1111-1111-1111-111111111111"), "Acme"),
        };
        var lookup = new LookupAttributeMetadata { LogicalName = "parentaccountid", Targets = ["account"] };

        var cancelTask = service.PickAsync(lookup, original, single: true, CancellationToken.None);
        PumpUntil(() => host.Content is ContentDialog || cancelTask.IsCompleted);
        if (cancelTask.IsFaulted)
            cancelTask.GetAwaiter().GetResult();
        var cancelDialog = Assert.IsType<ContentDialog>(host.Content);
        Assert.Equal("Add", cancelDialog.PrimaryButtonText);
        Assert.Equal("Cancel", cancelDialog.CloseButtonText);
        RealizeDialog(window, cancelDialog);
        var cancelPicker = Assert.IsType<LookupRecordPicker>(cancelDialog.Content);
        var cancelVm = Assert.IsType<LookupRecordPickerViewModel>(cancelPicker.DataContext);
        cancelVm.Selected.Add(new LookupRuleValue("account", Guid.Parse("22222222-2222-2222-2222-222222222222")));
        var cancelButton = FindButtons(window).Single(b => Equals(b.Content, "Cancel"));
        Assert.True(cancelButton.IsCancel);
        InvokeClick(cancelButton);
        PumpUntil(() => cancelTask.IsCompleted);
        Assert.Null(cancelTask.GetAwaiter().GetResult());
        Assert.Single(original);

        var addTask = service.PickAsync(lookup, original, single: true, CancellationToken.None);
        PumpUntil(() => host.Content is ContentDialog || addTask.IsCompleted);
        if (addTask.IsFaulted)
            addTask.GetAwaiter().GetResult();
        var addDialog = Assert.IsType<ContentDialog>(host.Content);
        RealizeDialog(window, addDialog);
        var addPicker = Assert.IsType<LookupRecordPicker>(addDialog.Content);
        var addVm = Assert.IsType<LookupRecordPickerViewModel>(addPicker.DataContext);
        PumpUntil(() => source.Completions.Count >= 2);
        source.Completions[^1].SetResult(new LookupRecordPage([], false, null));
        PumpUntil(() => !addVm.IsBusy && addVm.CanAccept);
        Flush();
        Assert.True(addDialog.IsPrimaryButtonEnabled);

        var searchBox = FindVisualChildren<TextBox>(addPicker).Single(t => t.MaxLength == 200);
        searchBox.Focus();
        Flush();
        Assert.True(searchBox.IsKeyboardFocused || searchBox.IsFocused || searchBox.IsKeyboardFocusWithin);
        searchBox.Text = "Acme";
        Flush();
        var requestsBeforeEnter = source.Requests.Count;
        var enter = addPicker.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.Enter);
        Assert.Same(addVm.SearchCommand, enter.Command);
        enter.Command.Execute(null);
        Assert.True(source.Requests.Count > requestsBeforeEnter);
        source.Completions[^1].SetResult(new LookupRecordPage([], false, null));
        PumpUntil(() => !addVm.IsBusy && addVm.CanAccept);
        Flush();

        var addButton = FindButtons(window).Single(b => Equals(b.Content, "Add"));
        InvokeClick(addButton);
        PumpUntil(() => addTask.IsCompleted);
        var accepted = addTask.GetAwaiter().GetResult();
        Assert.NotNull(accepted);
        Assert.Equal(original[0].Id, Assert.Single(accepted).Id);
        Assert.NotSame(original[0], accepted[0]);
        Assert.Single(original);

        var secondCancel = service.PickAsync(lookup, original, single: true, CancellationToken.None);
        PumpUntil(() => host.Content is ContentDialog || secondCancel.IsCompleted);
        if (secondCancel.IsFaulted)
            secondCancel.GetAwaiter().GetResult();
        var secondDialog = Assert.IsType<ContentDialog>(host.Content);
        RealizeDialog(window, secondDialog);
        secondDialog.TemplateButtonCommand.Execute(ContentDialogButton.Close);
        PumpUntil(() => secondCancel.IsCompleted);
        Assert.Null(secondCancel.GetAwaiter().GetResult());
        Assert.Empty(CapturedBindingErrors);
    }

    public void Dispose()
    {
        foreach (var window in _windows)
            window.Close();
        _windows.Clear();
    }

    private LookupRecordPicker LoadPicker(LookupRecordPickerViewModel vm)
    {
        EnsureApplication();
        StartBindingTrace();
        CapturedBindingErrors.Clear();
        var picker = new LookupRecordPicker { DataContext = vm };
        var window = new Window
        {
            Content = picker,
            Width = 700,
            Height = 520,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow,
        };
        _windows.Add(window);
        window.Show();
        picker.UpdateLayout();
        window.UpdateLayout();
        Flush();
        return picker;
    }

    private static void RealizeDialog(Window window, ContentDialog dialog)
    {
        window.UpdateLayout();
        dialog.ApplyTemplate();
        dialog.UpdateLayout();
        Flush();
    }

    private static void InvokeClick(ButtonBase button)
    {
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(button, null);
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root) => FindVisualChildren<Button>(root);

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        throw new InvalidOperationException($"No {typeof(T).Name} ancestor of {node.GetType().Name}.");
    }

    private static Color ExpectedColor(string resourceKey) =>
        Assert.IsType<SolidColorBrush>(Application.Current.TryFindResource(resourceKey)).Color;

    private static int CountGeneratedContainers(ItemsControl items)
    {
        var generated = 0;
        for (var i = 0; i < items.Items.Count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                generated++;
        }
        return generated;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Timed out waiting for UI condition.");
            PumpDispatcher();
        }
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class ScriptedLookupSource : ILookupRecordSource
    {
        public List<LookupSearchRequest> Requests { get; } = [];
        public List<TaskCompletionSource<LookupRecordPage>> Completions { get; } = [];

        public Task<LookupRecordPage> ReadAsync(LookupSearchRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var tcs = new TaskCompletionSource<LookupRecordPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Completions.Add(tcs);
            return tcs.Task;
        }
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

    // Profile path: the only one that creates the grouped ColumnsView.
    private (RulesPage page, RuleEditorViewModel vm) LoadRulesPageForProfileOnSta()
    {
        EnsureApplication();
        StartBindingTrace();
        CapturedBindingErrors.Clear();

        var name = new StringAttributeMetadata { LogicalName = "name", IsValidForCreate = true, MaxLength = 20 };
        var calc = new StringAttributeMetadata { LogicalName = "calc", IsValidForCreate = false, MaxLength = 20 };
        var account = new EntityMetadata { LogicalName = "account" };
        account.GetType().GetProperty("Attributes")!.SetValue(account, new AttributeMetadata[] { name, calc });
        var metadata = new Mock<IMetadataProvider>();
        metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([account]);
        var request = new RulesNavigationRequest
        {
            Profile = new Profile(1, "p", null, 42, [new ProfileTable("account", 10, null)]),
            TableName = "account",
        };
        var vm = new RuleEditorViewModel(metadata.Object, Mock.Of<IProfileService>(), Mock.Of<IAppNavigator>(), request);
        var load = vm.LoadForProfileAsync();
        PumpUntil(() => load.IsCompleted);
        load.GetAwaiter().GetResult();

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
        Flush();
        return (page, vm);
    }

    private static ToggleButton GroupHeader(DependencyObject root, string group) =>
        FindVisualChildren<ToggleButton>(root)
            .Single(t => t.DataContext is CollectionViewGroup g && Equals(g.Name, group));

    private (ConnectionsPage page, ConnectionManagerViewModel vm) LoadConnectionsPageOnSta()
    {
        EnsureApplication();
        StartBindingTrace();
        CapturedBindingErrors.Clear();

        var vm = new ConnectionManagerViewModel(
            Mock.Of<IConnectionProfileService>(),
            Mock.Of<IAuthService>(),
            Mock.Of<IDataverseConnectionService>());
        var page = new ConnectionsPage(vm);
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

    private static void SelectEndpoint(RuleEditorViewModel vm, string id)
    {
        vm.SelectedBogusEndpoint = id;
        Flush();
    }

    [StaFact]
    public void LookupValueBlock_EveryControl_UsesADgStyle()
    {
        var (page, vm) = LoadRulesPageOnSta(CustomerLookupColumn());
        vm.SelectedOp = "constant";
        Flush();

        var pane = FindVisualChild<RuleOperationPane>(page)!;
        var block = FindVisualChildren<StackPanel>(pane).Single(sp => sp.Name == "LookupValueBlock");
        var app = Application.Current!.Resources;

        AssertStyledBy(FindVisualChildren<ComboBox>(block), app["DG.ComboBox"], "ComboBox");
        AssertStyledBy(FindVisualChildren<TextBox>(block), app["DG.TextBox"], "TextBox");
        AssertStyledBy(FindVisualChildren<ListBox>(block), app["DG.ListBox"], "ListBox");
        foreach (var text in FindVisualChildren<TextBlock>(block))
        {
            if (text.TemplatedParent is not null) continue;
            Assert.True(ResolvesToADgStyle(text.Style, app),
                $"TextBlock '{text.Text}' has no DG.* style.");
        }
        Assert.Empty(CapturedBindingErrors);
    }

    private static void AssertStyledBy<T>(IEnumerable<T> controls, object expected, string label)
        where T : FrameworkElement
    {
        foreach (var control in controls)
        {
            var style = control.Style;
            Assert.True(ReferenceEquals(style, expected) || ReferenceEquals(style?.BasedOn, expected),
                $"{label} is not styled by the DG design system.");
        }
    }

    private static bool ResolvesToADgStyle(Style? style, ResourceDictionary app)
    {
        for (var current = style; current is not null; current = current.BasedOn)
            foreach (var key in new[] { "DG.Body", "DG.Secondary", "DG.Tertiary", "DG.GroupLabel", "DG.Mono", "DG.Cell" })
                if (ReferenceEquals(current, app[key]))
                    return true;
        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        => FindVisualChildren<T>(root).FirstOrDefault();

    private static LookupAttributeMetadata CustomerLookupColumn()
    {
        var attr = new LookupAttributeMetadata
        {
            LogicalName = "parentcustomerid",
            IsValidForCreate = true,
            Targets = ["account", "contact"],
        };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!
            .SetValue(attr, AttributeTypeCode.Customer);
        return attr;
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
