using Moq;
using Seedbomb.Services.Auth;
using Seedbomb.Services.Connections;
using Seedbomb.Services.Dataverse;
using Xunit;

namespace DataGen.Wpf.Tests;

public sealed class DataverseConnectionServiceTests
{
    [Fact]
    public async Task TokenProviderIgnoresCallerCancellationToken()
    {
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.GetTokenAsync(It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var profiles = new Mock<IConnectionProfileService>();
        using var sut = new DataverseConnectionService(auth.Object, profiles.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var provider = sut.CreateTokenProvider(["https://org.crm.dynamics.com/.default"]);
        var token = await provider("ignored");

        Assert.Equal("token", token);
        auth.Verify(
            a => a.GetTokenAsync(
                It.IsAny<string[]>(),
                CancellationToken.None),
            Times.Once);
    }
}
