// Adapted from PassportCheckerReborn b6bfcb4 (AGPL-3.0); see THIRD-PARTY-NOTICES.md.
using System.Text.Json;

namespace PartyScout.Core;

internal static class JsonExtensions
{
    internal static bool TryGetPath(this JsonElement element, out JsonElement result, params ReadOnlySpan<string> path)
    {
        result = element;
        foreach (var name in path)
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result)) return false;
        return result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
    internal static double? GetNumberOrNull(this JsonElement element, string name)
        => element.TryGetPath(out var value, name) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    internal static string? GetStringOrNull(this JsonElement element, string name)
        => element.TryGetPath(out var value, name) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
