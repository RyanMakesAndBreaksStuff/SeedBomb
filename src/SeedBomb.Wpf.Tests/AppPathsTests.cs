using SeedBomb.Core.Exceptions;
using Microsoft.Extensions.Logging;
using SeedBomb.Services.Diagnostics;
using SeedBomb.ViewModels;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Root_IsUnderSeedBombNotDataGen()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeedBomb");
        Assert.Equal(expected, AppPaths.Root.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(AppPaths.Root));
    }

    [Fact]
    public void Failed_legacy_move_keeps_using_the_legacy_folder_and_says_so()
    {
        // WR-008: the failed move was swallowed and an empty root created, so all data looked gone.
        var local = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "SeedBomb.Wpf.Tests", Guid.NewGuid().ToString("N"))).FullName;
        var legacy = Directory.CreateDirectory(Path.Combine(local, "DataGen")).FullName;
        try
        {
            // An open handle inside the folder makes the rename fail, as an antivirus scan would.
            using (File.Open(Path.Combine(legacy, "connections.json"), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var (root, warning) = AppPaths.Resolve(local);

                Assert.Equal(legacy, root);
                Assert.Contains(legacy, warning, StringComparison.Ordinal);
            }

            Assert.False(Directory.Exists(Path.Combine(local, "SeedBomb"))); // so next launch retries
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }
    }

    [Fact]
    public void FileLogger_WritesTheMessageAndException()
    {
        using var provider = new FileLoggerProvider();
        var logger = provider.CreateLogger("AppPathsTests");
        var marker = $"marker-{Guid.NewGuid():N}";

        logger.LogError(new InvalidOperationException("boom"), "Generation failed {Marker}", marker);

        var file = Directory.EnumerateFiles(AppPaths.Logs, "seedbomb-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var text = File.ReadAllText(file);
        Assert.Contains(marker, text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportRunFailure_ShowsTheGenerationExceptionMessage()
    {
        var vm = new RunViewModel();
        const string Message =
            "No readable existing candidates for 'test_project.test_application' (1500 planned rows).";

        vm.ReportRunFailure(new DataGenerationException(Message));

        Assert.Equal(Message, vm.LastFailureMessage);
    }

    [Fact]
    public void ReportRunFailure_PointsOtherFailuresAtTheLogFolder()
    {
        var vm = new RunViewModel();

        vm.ReportRunFailure(new InvalidOperationException("socket closed"));

        Assert.Contains("socket closed", vm.LastFailureMessage, StringComparison.Ordinal);
        Assert.Contains(AppPaths.Logs, vm.LastFailureMessage, StringComparison.Ordinal);
    }
}
