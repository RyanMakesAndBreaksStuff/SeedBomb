using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeedBomb.Core.Contracts;
using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SeedBomb.ViewModels.Controls;

/// <summary>ViewModel for <see cref="SeedBomb.Views.Controls.EntitySelectorControl"/>.</summary>
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

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FooterLabel))]
    private string _filterText = string.Empty;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FooterLabel))]
    private bool _showCustomOnly;

    /// <summary>Footer copy: "218 tables · 3 selected · custom".</summary>
    public string FooterLabel
    {
        get
        {
            var customMark = ShowCustomOnly || SelectedEntities.Any(e => e.IsCustom) ? " · custom" : "";
            return $"{Entities.Count} tables · {SelectedEntities.Count} selected{customMark}";
        }
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] [NotifyPropertyChangedFor(nameof(ShowContent))]
    private string? _errorMessage;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool _isLoading;

    /// <summary>Gets a value indicating whether an error message is present.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Gets whether the search bar and entity list should render. Sharing this Grid cell with
    /// the loading spinner and error banner without it lets the search row and error banner
    /// render stacked on top of each other whenever a load fails (IsLoading alone doesn't cover
    /// the errored-and-not-loading state) — and since this content is later in the XAML, it also
    /// sits above the error banner in z-order and swallows clicks meant for its Retry button.
    /// </summary>
    public bool ShowContent => !IsLoading && !HasError;

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
            OnPropertyChanged(nameof(FooterLabel));
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

    /// <summary>
    /// Programmatically sets the selection to exactly <paramref name="entities"/> — e.g. mirroring
    /// a profile import applied on the owning page's view-model. Checkbox state (<see cref="EntitySelectionItem.IsSelected"/>)
    /// and <see cref="SelectedEntities"/> are synced without duplicating or reordering rows already
    /// matching an entry in <paramref name="entities"/>. Does not raise <see cref="SelectedEntitiesChanged"/>:
    /// the caller already owns the resulting selection and does not need it echoed back.
    /// </summary>
    /// <param name="entities">The full desired selection.</param>
    public void SetSelection(IReadOnlyList<EntitySummary> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        _suppressSelectionEvents = true;
        try
        {
            var wanted = entities.Select(e => e.LogicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in EntityItems)
                item.IsSelected = wanted.Contains(item.LogicalName);

            SelectedEntities.Clear();
            foreach (var entity in entities)
                SelectedEntities.Add(entity);
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        ClearCheckedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FooterLabel));
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
        ClearCheckedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FooterLabel));
    }

    [RelayCommand]
    private void ToggleCustomOnly() => ShowCustomOnly = !ShowCustomOnly;

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearChecked() => ClearSelection();

    private bool CanClearSelection() => SelectedEntities.Count > 0;

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
        ClearCheckedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FooterLabel));
    }

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    partial void OnShowCustomOnlyChanged(bool value) => RefreshFilter();

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
        foreach (var item in EntityItems.Where(item => MatchesFilter(item.Entity, filter, ShowCustomOnly)))
            FilteredEntities.Add(item);
    }

    private static bool MatchesFilter(EntitySummary entity, string filter, bool customOnly)
    {
        if (customOnly && !entity.IsCustom)
            return false;
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

        [ObservableProperty] private bool _isSelected;
    }
}