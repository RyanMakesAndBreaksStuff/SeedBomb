namespace DataGen.Web.Tests;

public class GenerationServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static GenerationService BuildSut(
        Mock<IMetadataProvider>? metadataMock = null,
        Mock<IBulkCreator>? bulkCreatorMock = null)
    {
        metadataMock ??= new Mock<IMetadataProvider>();
        bulkCreatorMock ??= new Mock<IBulkCreator>();

        // Real concrete collaborators — these are not mockable (non-virtual) and are
        // cheap/side-effect-free on empty inputs.
        var graphBuilder = new GraphBuilder(NullLogger<GraphBuilder>.Instance);
        var cycleDetector = new CycleDetector(NullLogger<CycleDetector>.Instance);
        var topoSort = new TopologicalSort(NullLogger<TopologicalSort>.Instance);

        return new GenerationService(
            metadataMock.Object,
            graphBuilder,
            cycleDetector,
            topoSort,
            bulkCreatorMock.Object,
            NullLogger<GenerationService>.Instance);
    }

    // ── Happy path ─────────────────────────────────────────────────────────────

    /// <summary>
    /// GenerateAsync with an empty entity list should complete successfully,
    /// delegating to IBulkCreator and returning an empty result with no errors.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_EmptyConfig_ReturnsSuccessfulEmptyResult()
    {
        // Arrange
        var metadataMock = new Mock<IMetadataProvider>();
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EntityMetadata>());

        var bulkCreatorMock = new Mock<IBulkCreator>();
        bulkCreatorMock
            .Setup(b => b.CreateAsync(
                It.IsAny<GenerationConfig>(),
                It.IsAny<IReadOnlyDictionary<string, EntityMetadata>>(),
                It.IsAny<DependencyGraph>(),
                It.IsAny<IProgress<BulkCreationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GenerationResult
            {
                CreatedRecords = new Dictionary<string, IReadOnlyList<Guid>>(),
                Errors = [],
                Elapsed = TimeSpan.FromMilliseconds(1)
            });

        var sut = BuildSut(metadataMock, bulkCreatorMock);

        var config = new GenerationConfig
        {
            EntityLogicalNames = [],
            RecordCounts = new Dictionary<string, int>()
        };

        // Act
        var result = await sut.GenerateAsync(config);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0, result.TotalRecords);
        Assert.Empty(result.Errors);
        Assert.True(result.Elapsed >= TimeSpan.Zero);

        bulkCreatorMock.Verify(
            b => b.CreateAsync(
                config,
                It.IsAny<IReadOnlyDictionary<string, EntityMetadata>>(),
                It.IsAny<DependencyGraph>(),
                It.IsAny<IProgress<BulkCreationProgress>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Cancellation ───────────────────────────────────────────────────────────

    /// <summary>
    /// GenerateAsync with a pre-cancelled token must propagate OperationCanceledException.
    /// The metadata call is the first async point; it honours the token so the exception
    /// surfaces before any bulk creation work occurs.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var metadataMock = new Mock<IMetadataProvider>();
        metadataMock
            .Setup(m => m.GetEntitiesAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .Returns<string[], CancellationToken>((_, ct) =>
                Task.FromCanceled<IReadOnlyList<EntityMetadata>>(ct));

        var sut = BuildSut(metadataMock);

        var config = new GenerationConfig
        {
            EntityLogicalNames = ["account"],
            RecordCounts = new Dictionary<string, int> { ["account"] = 10 }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert — TaskCanceledException is a subclass of OperationCanceledException;
        // ThrowsAnyAsync matches either.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GenerateAsync(config, uiProgress: null, ct: cts.Token));
    }

    /// <summary>
    /// Contract test for A6: GenerationService should expose an async service-client
    /// resolution path via factory to avoid sync-over-async call chains.
    /// This test verifies that the async factory is invoked during generation.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ResolvesServiceClientViaAsyncFactory_WithoutBlockingSyncPath()
    {
        // Arrange: mock async factory delegate
        var asyncFactoryCalled = false;
        Func<CancellationToken, Task<IOrganizationServiceAsync2>> asyncFactory = async ct =>
        {
            asyncFactoryCalled = true;
            var mockService = new Mock<IOrganizationServiceAsync2>();
            mockService.Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            return mockService.Object;
        };

        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger<GenerationService>>();
        mockLoggerFactory.Setup(lf => lf.CreateLogger(It.IsAny<string>()))
            .Returns(mockLogger.Object);

        var service = new GenerationService(asyncFactory, mockLoggerFactory.Object, mockLogger.Object);

        // Act
        var ct = CancellationToken.None;
        var result = await service.GenerateAsync(
            orgId: "test-org",
            entityLogicalName: "account",
            recordCount: 1,
            ct: ct);

        // Assert
        Assert.True(asyncFactoryCalled, "Async factory delegate should have been invoked");
        Assert.NotNull(result);
    }
}
