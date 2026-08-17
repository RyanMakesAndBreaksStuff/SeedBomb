using DataGen.Core.Contracts;
using Moq;
using Seedbomb.Services.Generation;
using Seedbomb.ViewModels;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class RunViewModelTests
{
    [Fact]
    public void Classifier_ThrottleIsRetryable_DuplicateIsNot()
    {
        Assert.True(RejectionClassifier.IsRetryable(new BatchError("account", 0, "throttled 429", -2147220956)));
        Assert.False(RejectionClassifier.IsRetryable(new BatchError("account", 0, "Duplicate key on emailaddress1", null)));
    }

    [Fact]
    public void ApplyResult_GroupsByCause_AndDoesNotSelectFixFirst()
    {
        var vm = new RunViewModel();
        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["account"] = [Guid.NewGuid(), Guid.NewGuid()],
            },
            Elapsed = TimeSpan.FromSeconds(10),
            Errors =
            [
                new BatchError("account", 0, "Duplicate key on emailaddress1", null),
                new BatchError("account", 1, "Duplicate key on emailaddress1", null),
                new BatchError("account", 2, "request throttled", -2147220956),
            ],
        }, seed: 40719, environmentHost: "contoso-dev");

        Assert.Contains("Nothing was rolled back", vm.OutcomeDetail, StringComparison.Ordinal);
        Assert.Equal(2, vm.RejectionGroups.Count);
        var dup = Assert.Single(vm.RejectionGroups, g => !g.IsRetryable);
        Assert.False(dup.IsSelectedForRetry);
        Assert.False(dup.IsRetryable);
        var throttle = Assert.Single(vm.RejectionGroups, g => g.IsRetryable);
        Assert.True(throttle.IsSelectedForRetry);
        Assert.Contains("1 selected", vm.RetryButtonLabel, StringComparison.Ordinal);
        Assert.Equal("FixFirst", dup.DispositionKey);
        Assert.Equal("Retryable", throttle.DispositionKey);
    }

    [Fact]
    public async Task RetrySelected_CallsGenerationService_WithoutGenerateViewModel()
    {
        var gen = new Mock<IWpfGenerationService>();
        gen.Setup(g => g.GenerateAsync(
                It.IsAny<GenerationConfig>(),
                It.IsAny<IProgress<ProgressUpdate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
                Elapsed = TimeSpan.FromSeconds(1),
                Errors = [],
            });

        var vm = new RunViewModel(generation: gen.Object);
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 40719,
        };
        vm.ApplyResult(new GenerationResult
        {
            CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
            Elapsed = TimeSpan.FromSeconds(1),
            Errors = [new BatchError("account", 2, "request throttled", -2147220956)],
        }, seed: 40719, environmentHost: "contoso-dev", config: config);

        await vm.RetrySelectedCommand.ExecuteAsync(null);

        gen.Verify(g => g.GenerateAsync(
            It.Is<GenerationConfig>(c => c.EntityLogicalNames.Contains("account")),
            It.IsAny<IProgress<ProgressUpdate>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RiskyBogusDeclined_MakesZeroGenerateCalls()
    {
        var gen = new Mock<IWpfGenerationService>();
        var vm = new RunViewModel(generation: gen.Object)
        {
            ConfirmRiskyBogus = () => Task.FromResult(false),
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 7,
            FieldRules = new Dictionary<string, Dictionary<string, DataGen.Core.Rules.FieldRule>>
            {
                ["account"] = new() { ["emailaddress1"] = new DataGen.Core.Rules.BogusRule("INTERNET", "email", 1) },
            },
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.ExecuteAsync(config, "contoso-dev", ["account"], 3, TestContext.Current.CancellationToken));

        gen.Verify(g => g.GenerateAsync(
            It.IsAny<GenerationConfig>(),
            It.IsAny<IProgress<ProgressUpdate>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_RePromptsRiskyBogus_AndCancelledMakesZeroCalls()
    {
        var gen = new Mock<IWpfGenerationService>();
        var prompted = 0;
        var vm = new RunViewModel(generation: gen.Object)
        {
            ConfirmRiskyBogus = () =>
            {
                prompted++;
                return Task.FromResult(false);
            },
        };
        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 3 },
            Seed = 7,
            AllowRiskyBogusValues = true,
            FieldRules = new Dictionary<string, Dictionary<string, DataGen.Core.Rules.FieldRule>>
            {
                ["account"] = new() { ["emailaddress1"] = new DataGen.Core.Rules.BogusRule("INTERNET", "email", 1) },
            },
        };
        vm.ApplyResult(new GenerationResult
        {
            Errors = [new BatchError("account", 2, "request throttled", -2147220956)],
        }, seed: 7, environmentHost: "contoso-dev", config: config);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => vm.RetrySelectedCommand.ExecuteAsync(null));

        Assert.Equal(1, prompted);
        gen.Verify(g => g.GenerateAsync(
            It.IsAny<GenerationConfig>(),
            It.IsAny<IProgress<ProgressUpdate>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
