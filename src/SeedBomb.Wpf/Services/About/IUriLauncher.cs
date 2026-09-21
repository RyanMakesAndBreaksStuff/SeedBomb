using System.Diagnostics;

namespace SeedBomb.Services.About;

/// <summary>Opens an external https URL in the system browser.</summary>
public interface IUriLauncher
{
    /// <summary>Returns whether <paramref name="url"/> is a safe https URL.</summary>
    bool CanOpen(string? url);

    /// <summary>Opens <paramref name="url"/>. Returns false and an error when it cannot.</summary>
    bool TryOpen(string? url, out string error);
}

/// <summary>Launches https URLs with <c>UseShellExecute</c>.</summary>
public sealed class ProcessUriLauncher : IUriLauncher
{
    /// <inheritdoc />
    public bool CanOpen(string? url) => TryCreateHttps(url, out _);

    /// <inheritdoc />
    public bool TryOpen(string? url, out string error)
    {
        if (!TryCreateHttps(url, out var uri))
        {
            error = "Only https links can be opened.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryCreateHttps(string? url, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(url)
               && Uri.TryCreate(url, UriKind.Absolute, out uri!)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}