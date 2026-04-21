namespace DataGen.Web.Hubs;

/// <summary>Strongly-typed hub method name constants.</summary>
public static class HubMethods
{
    /// <summary>Progress update event.</summary>
    public const string OnProgress = "OnProgress";

    /// <summary>Generation phase changed.</summary>
    public const string OnPhaseChange = "OnPhaseChange";

    /// <summary>Error occurred during generation.</summary>
    public const string OnError = "OnError";

    /// <summary>Generation completed.</summary>
    public const string OnComplete = "OnComplete";
}
