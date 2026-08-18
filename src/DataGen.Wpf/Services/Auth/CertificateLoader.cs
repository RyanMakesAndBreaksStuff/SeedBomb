using System.Security.Cryptography.X509Certificates;

namespace Seedbomb.Services.Auth;

/// <summary>Resolves a client certificate from the CurrentUser store by thumbprint.</summary>
public static class CertificateLoader
{
    /// <summary>
    /// Normalises a thumbprint for store lookup: strips whitespace and the invisible
    /// left-to-right marks the Windows certificate dialog inserts on copy, then uppercases.
    /// </summary>
    /// <param name="thumbprint">Raw thumbprint text.</param>
    /// <returns>The normalised thumbprint.</returns>
    public static string Normalize(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);
        return new string([.. thumbprint.Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
    }

    /// <summary>Loads the certificate with the given thumbprint from CurrentUser\My.</summary>
    /// <param name="thumbprint">Certificate thumbprint; spacing and case are ignored.</param>
    /// <returns>The matching certificate.</returns>
    /// <exception cref="InvalidOperationException">No matching certificate is installed.</exception>
    public static X509Certificate2 Load(string thumbprint)
    {
        var normalized = Normalize(thumbprint);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var match = store.Certificates
            .Cast<X509Certificate2>()
            .FirstOrDefault(c => string.Equals(
                Normalize(c.Thumbprint), normalized, StringComparison.Ordinal));

        return match ?? throw new InvalidOperationException(
            $"Certificate with thumbprint '{normalized}' was not found in the current user's personal store.");
    }
}
