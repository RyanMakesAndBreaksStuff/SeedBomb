using Seedbomb.Services.Profiles;

namespace Seedbomb.Services.Navigation;

/// <summary>One-shot payload for RulesPage. Written by the caller, consumed in OnNavigatedToAsync.</summary>
public sealed class RulesNavigationRequest
{
    /// <summary>Profile being edited, or null when opened from bare nav.</summary>
    public Profile? Profile { get; set; }

    /// <summary>Preferred table logical name.</summary>
    public string? TableName { get; set; }

    /// <summary>Invoked after a successful Save on the Rules page (Generate draft sync).</summary>
    public Action<Profile>? OnSaved { get; set; }

    /// <summary>Clears the payload after the page reads it.</summary>
    public void Clear()
    {
        Profile = null;
        TableName = null;
        OnSaved = null;
    }
}
