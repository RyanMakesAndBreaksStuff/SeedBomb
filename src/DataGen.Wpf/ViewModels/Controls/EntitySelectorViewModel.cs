using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Contracts;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace DataGen.Wpf.ViewModels.Controls;

/// <summary>ViewModel for <see cref="DataGen.Wpf.Views.Controls.EntitySelectorControl"/>.</summary>
public sealed partial class EntitySelectorViewModel : ObservableObject
{
    private readonly IMetadataProvider _metadata;
    private readonly ILogger<EntitySelectorViewModel> _logger;
    private CancellationTokenSource? _loadCts;

    /// <summary>Initialises the view-model.</summary>
    /// <param name="metadata">Metadata provider for listing user entities.</param>
    /// <param name="logger">Logger.</param>
    public EntitySelectorViewModel(IMetadataProvider metadata, ILogger<EntitySelectorViewModel> logger)
    {
        _metadata = metadata;
        _logger = logger;
    }

    /// <summary>Raised when the entity selection changes.</summary>
    public event EventHandler<IReadOnlyList<EntitySummary>>? SelectedEntitiesChanged;

    /// <summary>All available user entities, sorted by display name.</summary>
    public ObservableCollection<EntitySummary> Entities { get; } = [];

    /// <summary>Currently selected entities.</summary>
    public ObservableCollection<EntitySummary> SelectedEntities { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Gets a value indicating whether an error message is present.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>Loads the entity list from Dataverse. Called once on control load.</summary>
    [RelayCommand]
    public async Task LoadEntitiesAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        ErrorMessage = null;
        Entities.Clear();

        try
        {
            var list = await _metadata.ListUserEntitiesAsync(ct);
            foreach (var entity in list.OrderBy(e => e.DisplayName))
                Entities.Add(entity);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Entity list load cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load entity list");
            ErrorMessage = $"Failed to load entities: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Toggles the selection state of an entity.</summary>
    /// <param name="entity">The entity to toggle.</param>
    public void ToggleSelection(EntitySummary entity)
    {
        if (SelectedEntities.Contains(entity))
            SelectedEntities.Remove(entity);
        else
            SelectedEntities.Add(entity);

        SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntities]);
    }
}
