using SeedBomb.Services.About;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class UriLauncherTests
{
    [Theory]
    [InlineData("https://github.com/bchavez/Bogus")]
    [InlineData("https://github.com/RyanMakesAndBreaksStuff/SeedBomb")]
    public void CanOpen_https_without_userinfo(string url) =>
        Assert.True(new ProcessUriLauncher().CanOpen(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://github.com/bchavez/Bogus")]
    [InlineData("file:///C:/secrets")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@example.com")]
    public void CanOpen_rejects_unsafe_urls(string? url) =>
        Assert.False(new ProcessUriLauncher().CanOpen(url));
}
