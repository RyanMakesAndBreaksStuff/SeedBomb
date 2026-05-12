namespace DataGen.Web.Tests;

public class GenerationServiceTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a GenerationService instance with mocked dependencies for testing.
    /// Uses real, non-virtual collaborators (GraphBuilder, CycleDetector, TopologicalSort)
    /// which are side-effect-free and cheap to construct.
    /// </summary>
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

    /// <summary>
    /// Creates a mocked <see cref="ILoggerFactory"/> that returns a mocked
    /// <see cref="ILogger{T}"/> for GenerationService. Used by tests that require
    /// explicit logger control (e.g., async factory verification).
    /// </summary>
    private Mock<ILoggerFactory> CreateMockLoggerFactory()
    {
        // Mocks the ILoggerFactory to ensure predictable logger instances during testing.
        var mockLoggerFactory = new Mock<ILoggerFactory>();
        var mockLogger = new Mock<ILogger<GenerationService>>();
        mockLoggerFactory.Setup(lf => lf.CreateLogger(It.IsAny<string>()))
            .Returns(mockLogger.Object);
        return mockLoggerFactory;
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
    /// GenerationService has a direct pipeline constructor and an async-factory
    /// constructor for focused tests. The app DI path must resolve the direct
    /// pipeline constructor even when both dependency sets are registered.
    /// </summary>
    [Fact]
    public void ServiceProvider_ResolvesGenerationService_WhenBothConstructorDependencySetsAreRegistered()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new Mock<IMetadataProvider>().Object);
        services.AddScoped(_ => new Mock<IBulkCreator>().Object);
        services.AddScoped<GraphBuilder>();
        services.AddScoped<CycleDetector>();
        services.AddScoped<TopologicalSort>();
        services.AddScoped<Func<CancellationToken, Task<IOrganizationServiceAsync2>>>(_ =>
            _ => Task.FromResult(new Mock<IOrganizationServiceAsync2>().Object));
        services.AddScoped<GenerationService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Act
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<GenerationService>();

        // Assert
        Assert.NotNull(service);
    }

    /// <summary>
    /// Contract test for A6: GenerationService should expose an async service-client
    /// resolution path via factory to avoid sync-over-async call chains.
    /// This test verifies that:
    /// 1. The async factory is invoked during generation (not a sync fallback path)
    /// 2. The result is a valid GenerationResult with expected structure
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ResolvesServiceClientViaAsyncFactory_WithoutBlockingSyncPath()
    {
        // Arrange: Mock async factory to track invocation and provide a mocked service client.
        // The factory must be async to prevent blocking sync-over-async patterns.
        var asyncFactoryCalled = false;
        Func<CancellationToken, Task<IOrganizationServiceAsync2>> asyncFactory = async ct =>
        {
            asyncFactoryCalled = true;
            var mockService = new Mock<IOrganizationServiceAsync2>();

            // Mock ExecuteAsync so DataverseMetadataProvider can resolve entity metadata.
            var entityMeta = new EntityMetadata { LogicalName = "account" };
            var metaResponse = new RetrieveEntityResponse();
            metaResponse.Results["EntityMetadata"] = entityMeta;
            mockService
                .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(metaResponse);

            // Mock CreateAsync to return a valid GUID for each record creation.
            mockService.Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            return mockService.Object;
        };

        // Mock ILoggerFactory to provide predictable logging during the test.
        var mockLoggerFactory = CreateMockLoggerFactory();

        var service = new GenerationService(asyncFactory, mockLoggerFactory.Object);

        // Act
        var ct = CancellationToken.None;
        var result = await service.GenerateAsync(
            orgId: "test-org",
            entityLogicalName: "account",
            recordCount: 1,
            ct: ct);

        // Assert
        // Verify the async factory was invoked (proving async path, not sync fallback).
        Assert.True(asyncFactoryCalled, "Async factory delegate should have been invoked");

        // Verify the result is a valid GenerationResult instance with expected structure.
        Assert.NotNull(result);
        Assert.IsType<GenerationResult>(result);
        Assert.NotNull(result.CreatedRecords);
        Assert.NotNull(result.Errors);
        Assert.True(result.Elapsed >= TimeSpan.Zero, "Elapsed time must be non-negative");
    }
}
