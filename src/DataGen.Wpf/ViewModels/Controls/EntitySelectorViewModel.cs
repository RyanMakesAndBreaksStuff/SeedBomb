using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataGen.Core.Contracts;
using DataGen.Core.Metadata;
using Microsoft.Extensions.Logging;

namespace DataGen.Desktop.ViewModels.Controls;

/// <summary>ViewModel for <see cref="DataGen.Desktop.Views.Controls.EntitySelectorControl"/>.</summary>
public sealed partial class EntitySelectorViewModel : ObservableObject
{
    private readonly IMetadataProvider _metadata;
    private readonly ILogger<EntitySelectorViewModel> _logger;
    private CancellationTokenSource? _loadCts;
    private bool _suppressSelectionEvents;

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

    /// <summary>All selectable entity rows, sorted by display name.</summary>
    public ObservableCollection<EntitySelectionItem> EntityItems { get; } = [];

    /// <summary>Entity rows matching the current search text.</summary>
    public ObservableCollection<EntitySelectionItem> FilteredEntities { get; } = [];

    /// <summary>Currently selected entities.</summary>
    public ObservableCollection<EntitySummary> SelectedEntities { get; } = [];

    [ObservableProperty]
    private string _filterText = string.Empty;

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
        ClearItems();
        Entities.Clear();

        try
        {
            var list = await _metadata.ListUserEntitiesAsync(ct);
            foreach (var entity in list.OrderBy(e => e.DisplayName))
            {
                Entities.Add(entity);
                AddItem(entity);
            }

            RefreshFilter();
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
        var item = EntityItems.FirstOrDefault(i => i.Entity == entity);
        if (item is not null)
        {
            item.IsSelected = !item.IsSelected;
            return;
        }

        ToggleSelectedEntity(entity);
    }

    /// <summary>Clears all selected entities.</summary>
    public void ClearSelection()
    {
        if (SelectedEntities.Count == 0)
            return;

        _suppressSelectionEvents = true;
        try
        {
            SelectedEntities.Clear();
            foreach (var item in EntityItems)
                item.IsSelected = false;
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        SelectedEntitiesChanged?.Invoke(this, []);
    }

    private void ToggleSelectedEntity(EntitySummary entity)
    {
        if (SelectedEntities.Contains(entity))
            SelectedEntities.Remove(entity);
        else
            SelectedEntities.Add(entity);

        SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntities]);
    }

    private void SetSelectedEntity(EntitySummary entity, bool isSelected)
    {
        if (isSelected)
        {
            if (!SelectedEntities.Contains(entity))
                SelectedEntities.Add(entity);
        }
        else
        {
            SelectedEntities.Remove(entity);
        }
    }

    private void RaiseSelectionChanged()
    {
        if (_suppressSelectionEvents)
            return;

        SelectedEntitiesChanged?.Invoke(this, [.. SelectedEntities]);
    }

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    private void AddItem(EntitySummary entity)
    {
        var item = new EntitySelectionItem(entity);
        item.PropertyChanged += OnItemPropertyChanged;
        EntityItems.Add(item);
    }

    private void ClearItems()
    {
        foreach (var item in EntityItems)
            item.PropertyChanged -= OnItemPropertyChanged;

        EntityItems.Clear();
        FilteredEntities.Clear();
        SelectedEntities.Clear();
    }

    private void RefreshFilter()
    {
        FilteredEntities.Clear();
        var filter = FilterText.Trim();
        foreach (var item in EntityItems.Where(item => MatchesFilter(item.Entity, filter)))
            FilteredEntities.Add(item);
    }

    private static bool MatchesFilter(EntitySummary entity, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return entity.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entity.LogicalName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EntitySelectionItem.IsSelected) || sender is not EntitySelectionItem item)
            return;

        SetSelectedEntity(item.Entity, item.IsSelected);
        RaiseSelectionChanged();
    }

    /// <summary>A selectable row in the entity picker.</summary>
    public sealed partial class EntitySelectionItem(EntitySummary entity) : ObservableObject
    {
        /// <summary>Gets the entity represented by this row.</summary>
        public EntitySummary Entity { get; } = entity;

        /// <summary>Gets the display label for binding.</summary>
        public string DisplayName => Entity.DisplayName;

        /// <summary>Gets the logical name for binding and searching.</summary>
        public string LogicalName => Entity.LogicalName;

        /// <summary>Gets whether this row represents a custom entity.</summary>
        public bool IsCustom => Entity.IsCustom;

        [ObservableProperty]
        private bool _isSelected;
    }
}
