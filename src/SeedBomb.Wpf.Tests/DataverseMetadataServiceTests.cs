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
        _ = await Record.ExceptionAsync(async () => { await oldRead; });
        Assert.Same(currentMetadata, await service.GetEntityAsync("account", TestContext.Current.CancellationToken));
    }
}
