using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataGen.Core.Rules;

/// <summary>A lookup identity with an optional, non-authoritative display hint.</summary>
/// <param name="Entity">Canonical target logical name.</param>
/// <param name="Id">Non-empty record id.</param>
/// <param name="Name">Optional cached display name.</param>
public sealed record LookupRuleValue(string Entity, Guid Id, string? Name = null)
{
    /// <summary>Creates the SDK value; display hints are not sent to Dataverse.</summary>
    public EntityReference ToReference() => new(Entity, Id);

    /// <summary>Creates an owned JSON value for the existing rule wire format.</summary>
    public JsonElement ToJson() => Name is null
        ? JsonSerializer.SerializeToElement(new { entity = Entity, id = Id.ToString("D") })
        : JsonSerializer.SerializeToElement(new { entity = Entity, id = Id.ToString("D"), name = Name });

    /// <summary>Validates and normalizes an identity against current target metadata.</summary>
    /// <param name="json">Authored value.</param>
    /// <param name="metadata">Lookup metadata.</param>
    /// <param name="value">Normalized value on success.</param>
    /// <param name="error">Actionable reason on failure.</param>
    public static bool TryParse(JsonElement json, LookupAttributeMetadata metadata,
        out LookupRuleValue? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        value = null;
        error = "Expected an object with an allowed entity and a non-empty GUID id.";
        if (json.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
            if (!keys.Add(property.Name) || property.Name is not ("entity" or "id" or "name"))
                return false;
        if (!json.TryGetProperty("entity", out var entity) || entity.ValueKind != JsonValueKind.String
            || !json.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParse(id.GetString(), out var parsedId) || parsedId == Guid.Empty)
            return false;
        var target = (metadata.Targets ?? []).FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t)
            && string.Equals(t, entity.GetString(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            error = $"Entity '{entity.GetString()}' is not an allowed target of '{metadata.LogicalName}'.";
            return false;
        }
        string? name = null;
        if (json.TryGetProperty("name", out var hint))
        {
            if (hint.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            name = hint.ValueKind == JsonValueKind.String ? hint.GetString() : null;
        }
        value = new(target.ToLowerInvariant(), parsedId, name);
        error = null;
        return true;
    }
}
