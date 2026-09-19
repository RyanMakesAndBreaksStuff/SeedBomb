using DataGen.Core.Rules;
using Microsoft.Xrm.Sdk.Metadata;
using Seedbomb.ViewModels.Controls;
using Seedbomb.Views.Controls;
using System.Windows.Data;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Seedbomb.Services.Dataverse;

/// <summary>Uses the application's existing WPF-UI ContentDialogHost.</summary>
/// <param name="dialogs">Existing app-wide dialog service.</param>
/// <param name="createViewModel">DI factory for a fresh dialog view-model.</param>
public sealed class LookupRecordPickerService(
    IContentDialogService dialogs,
    Func<LookupRecordPickerViewModel> createViewModel) : ILookupRecordPicker
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<LookupRuleValue>?> PickAsync(LookupAttributeMetadata attribute,
        IReadOnlyList<LookupRuleValue> initial, bool single, CancellationToken ct)
    {
        using var vm = createViewModel();
        vm.Initialize(attribute, initial, single, ct);
        var dialog = new ContentDialog
        {
            Title = single ? "Choose one record" : "Choose at least two records",
            Content = new LookupRecordPicker { DataContext = vm },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
        };
        dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty,
            new Binding(nameof(vm.CanAccept)) { Source = vm, Mode = BindingMode.OneWay });
        var load = vm.SearchCommand.ExecuteAsync(null);
        try
        {
            var result = await dialogs.ShowAsync(dialog, ct);
            ct.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary && vm.CanAccept
                ? Array.AsReadOnly(vm.Selected.Select(v => v with { }).ToArray())
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            // LoadPageAsync observes/logs all failures; closing does not await an uncooperative read.
            _ = load;
        }
    }
}