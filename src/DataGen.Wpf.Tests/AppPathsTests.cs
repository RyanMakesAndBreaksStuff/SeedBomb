using System.IO;
using DataGen.Core.Contracts;
using DataGen.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Seedbomb.Services.Diagnostics;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

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
