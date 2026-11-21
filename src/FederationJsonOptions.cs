using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Extensions.Json.Converters;

namespace Jellyfin.Plugin.Federation;

/// <summary>
/// JSON options matching the Jellyfin server API contract (camelCase, enums as strings).
/// </summary>
public static class FederationJsonOptions
{
    /// <summary>
    /// Gets the shared serialization options.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The remote API serializes Guids in the N-format (32 hex chars, no
        // dashes) via the server's JsonGuidConverter — System.Text.Json's
        // built-in Guid converter rejects that, so reuse the server's converters.
        Converters = { new JsonStringEnumConverter(), new JsonGuidConverter(), new JsonNullableGuidConverter() }
    };
}
