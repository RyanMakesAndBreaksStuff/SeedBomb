using Seedbomb.Services.Auth;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace SeedBomb.Wpf.Tests;

public sealed class CertificateLoaderTests
{
    [Theory]
    [InlineData("A1 B2 C3", "A1B2C3")]
    [InlineData("a1b2c3", "A1B2C3")]
    [InlineData("\u200E A1B2C3 ", "A1B2C3")]
    public void Normalize_StripsSpacesAndLeftToRightMarksAndUppercases(string input, string expected)
        => Assert.Equal(expected, CertificateLoader.Normalize(input));

    [Fact]
    public void Load_Throws_WhenThumbprintIsNotInTheStore()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CertificateLoader.Load("0000000000000000000000000000000000000000"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_Throws_WithMessageNamingTheThumbprint()
    {
        const string thumbprint = "0000000000000000000000000000000000000000";

        var ex = Assert.Throws<InvalidOperationException>(() => CertificateLoader.Load(thumbprint));

        Assert.Contains(thumbprint, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_ReturnsCertificate_ThatOutlivesTheStoreAndCanSign()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=SeedBomb.Wpf CertificateLoader Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        // Re-load as a persisted (non-ephemeral), exportable key so it behaves like a real
        // certificate once it's added to the CurrentUser\My store below.
        using var installable = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(installable);

        try
        {
            using var loaded = CertificateLoader.Load(installable.Thumbprint);

            // The store CertificateLoader.Load opened internally is already closed by this
            // point - this is the exact bug being guarded against.
            Assert.True(loaded.HasPrivateKey);

            using var privateKey = loaded.GetRSAPrivateKey();
            Assert.NotNull(privateKey);

            var data = "sign-after-store-dispose"u8.ToArray();
            var signature = privateKey!.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            using var publicKey = loaded.GetRSAPublicKey();
            Assert.True(publicKey!.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        finally
        {
            store.Remove(installable);
        }
    }
}
