using System.IO;
using System.Reflection;

namespace SeedBomb.Services.About;

/// <summary>Reads embedded UTF-8 resources by logical name.</summary>
public interface IEmbeddedResourceReader
{
    /// <summary>Returns the resource text, or <c>null</c> if it is missing.</summary>
    string? ReadUtf8(string logicalName);
}

/// <summary>Reads resources from a specific assembly.</summary>
public sealed class AssemblyResourceReader(Assembly assembly) : IEmbeddedResourceReader
{
    /// <inheritdoc />
    public string? ReadUtf8(string logicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        using var stream = assembly.GetManifestResourceStream(logicalName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}