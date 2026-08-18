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
    /// <returns>
    /// A clone of the matching certificate that stays valid after the underlying store is
    /// closed.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No matching certificate is installed, or the match has no usable private key.
    /// </exception>
    public static X509Certificate2 Load(string thumbprint)
    {
        var normalized = Normalize(thumbprint);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        X509Certificate2? clone = null;

        // store.Certificates materialises every certificate in the store up front, so every
        // entry - matched or not - owns a native handle that must be disposed here; only the
        // clone we hand back should outlive this method.
        foreach (var candidate in store.Certificates.Cast<X509Certificate2>())
        {
            using (candidate)
            {
                if (clone is not null || !string.Equals(
                    Normalize(candidate.Thumbprint), normalized, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!candidate.HasPrivateKey)
                {
                    throw new InvalidOperationException(
                        $"Certificate with thumbprint '{normalized}' was found in the current user's " +
                        "personal store but has no usable private key.");
                }

                // The copy constructor duplicates the native certificate context (e.g.
                // CertDuplicateCertificateContext on Windows) instead of exporting and
                // re-importing key bytes, so it keeps working for non-exportable
                // (hardware- or TPM-backed) private keys and remains valid once `candidate`
                // and `store` are disposed.
                clone = new X509Certificate2(candidate);
            }
        }

        return clone ?? throw new InvalidOperationException(
            $"Certificate with thumbprint '{normalized}' was not found in the current user's personal store.");
    }
}
