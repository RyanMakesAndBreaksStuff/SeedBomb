using SeedBomb.Core.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using SeedBomb.Services.Dataverse;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class DataverseMetadataServiceTests
{
    [Fact]
    public async Task Old_read_cannot_repopulate_metadata_after_connection_reset()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<OrganizationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldMetadata = new EntityMetadata { LogicalName = "account" };
        var currentMetadata = new EntityMetadata { LogicalName = "account" };
        var oldService = new Mock<IOrganizationServiceAsync2>();
        oldService.Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => started.TrySetResult(true)).Returns(oldResponse.Task);
        var currentService = new Mock<IOrganizationServiceAsync2>();
        currentService.Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrieveEntityResponse { Results = { ["EntityMetadata"] = currentMetadata } });
        var connection = new Mock<IDataverseConnectionService>();
        connection.SetupSequence(c => c.GetOrganizationServiceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldService.Object).ReturnsAsync(currentService.Object);
        using var service = new DataverseMetadataService(connection.Object,
            NullLogger<DataverseMetadataProvider>.Instance);
        var oldRead = service.GetEntityAsync("account", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        connection.Raise(c => c.ConnectionReset += null, EventArgs.Empty);
        Assert.Same(currentMetadata, await service.GetEntityAsync("account", TestContext.Current.CancellationToken));
        oldResponse.SetResult(new RetrieveEntityResponse { Results = { ["EntityMetadata"] = oldMetadata } });
        // The in-flight read completes with what it fetched; it writes only to the retired cache.
        Assert.Same(oldMetadata, await oldRead);
        Assert.Same(currentMetadata, await service.GetEntityAsync("account", TestContext.Current.CancellationToken));
        currentService.Verify(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Read_in_flight_when_service_is_disposed_returns_fetched_metadata()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<OrganizationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new EntityMetadata { LogicalName = "account" };
        var org = new Mock<IOrganizationServiceAsync2>();
        org.Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => started.TrySetResult(true)).Returns(response.Task);
        var connection = new Mock<IDataverseConnectionService>();
        connection.Setup(c => c.GetOrganizationServiceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(org.Object);
        var service = new DataverseMetadataService(connection.Object, NullLogger<DataverseMetadataProvider>.Instance);

        var read = service.GetEntityAsync("account", TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        service.Dispose();
        response.SetResult(new RetrieveEntityResponse { Results = { ["EntityMetadata"] = metadata } });

        Assert.Same(metadata, await read);
    }

    [Fact]
    public void Dispose_is_idempotent_and_a_later_reset_is_ignored()
    {
        var connection = new Mock<IDataverseConnectionService>();
        var service = new DataverseMetadataService(connection.Object, NullLogger<DataverseMetadataProvider>.Instance);

        service.Dispose();
        service.Dispose();
        connection.Raise(c => c.ConnectionReset += null, EventArgs.Empty);
    }
}
