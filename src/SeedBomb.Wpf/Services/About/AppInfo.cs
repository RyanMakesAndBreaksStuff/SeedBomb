using System.Reflection;
using System.Runtime.InteropServices;

namespace Seedbomb.Services.About;

/// <summary>Canonical SeedBomb identity and project links.</summary>
public static class AppInfo
{
    public const string ProductName = "SeedBomb";
    public const string Description = "Synthetic data generation for Microsoft Dataverse";
    public const string Author = "Ryan Rettinger";
    public const string LicenseId = "BSD-3-Clause";
    public const string SourceCodeUrl = "https://github.com/RyanMakesAndBreaksStuff/DataGen";
    public const string DocumentationUrl = "https://github.com/RyanMakesAndBreaksStuff/DataGen/blob/master/README.md";
    public const string IssuesUrl = "https://github.com/RyanMakesAndBreaksStuff/DataGen/issues/new";

    public static string ReadVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus < 0 ? info : info[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static string ReadRuntime() => RuntimeInformation.FrameworkDescription;
}