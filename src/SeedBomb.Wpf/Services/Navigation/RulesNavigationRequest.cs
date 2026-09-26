using SeedBomb.Services.Profiles;

namespace SeedBomb.Services.Navigation;

/// <summary>One-shot payload for RulesPage. Written by the caller, consumed in OnNavigatedToAsync.</summary>
public sealed class RulesNavigationRequest
{
    /// <summary>Profile being edited, or null when opened from bare nav.</summary>
    public Profile? Profile { get; set; }

    /// <summary>Preferred table logical name.</summary>
    public string? TableName { get; set; }

    /// <summary>Invoked after a successful Save on the Rules page (Generate draft sync).</summary>
    public Action<Profile>? OnSaved { get; set; }

    /// <summary>Page to navigate after breadcrumb Back. Null means ProfilesPage.</summary>
    public Type? ReturnPage { get; set; }

    /// <summary>
    /// True when <see cref="Profile"/> is a saved profile, so rule edits are written back to its
    /// file. False for Generate's in-memory working set, which is updated through
    /// <see cref="OnSaved"/> only.
    /// </summary>
    public bool IsStored { get; set; }

    /// <summary>Clears the payload after the page reads it.</summary>
    public void Clear()
    {
        Profile = null;
        TableName = null;
        OnSaved = null;
        ReturnPage = null;
        IsStored = false;
    }
}