using System.Runtime.CompilerServices;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class ProjectLicenseTests
{
    [Fact]
    public void Repository_declares_bsd3_and_not_agpl()
    {
        var root = FindRepoRoot();
        var license = Path.Combine(root, "LICENSE");
        Assert.True(File.Exists(license), "Expected LICENSE at the repository root.");
        var text = File.ReadAllText(license);
        Assert.StartsWith("BSD 3-Clause License", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GNU AFFERO GENERAL PUBLIC LICENSE", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "LICENSE.txt")));
        Assert.False(File.Exists(Path.Combine(root, "LICENSE.TXT")));
        Assert.False(Directory.EnumerateFiles(root, "*AGPL*", SearchOption.TopDirectoryOnly).Any());

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.Contains("## License", readme, StringComparison.Ordinal);
        Assert.Contains("BSD-3-Clause", readme, StringComparison.Ordinal);
        Assert.Contains("[LICENSE](LICENSE)", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("AGPL", readme, StringComparison.Ordinal);
    }

    private static string FindRepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SeedBomb.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
