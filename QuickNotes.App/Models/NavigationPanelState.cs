using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNotes.App.Models;

/// <summary>
/// Navigation panel presentation state (§4.2):
/// <see cref="Normal"/>: full titles and counters;
/// <see cref="Compact"/>: short title / icon with tooltip and accessible name;
/// <see cref="Hidden"/>: only in fullscreen Focus scenario, with accessible command to restore.
/// </summary>
[JsonConverter(typeof(NavigationPanelStateJsonConverter))]
public enum NavigationPanelState
{
    Normal = 0,
    Compact = 1,
    Hidden = 2
}

/// <summary>
/// Fail-safe JSON converter: missing, unknown, corrupt, or out-of-range values
/// resolve to <see cref="NavigationPanelState.Normal"/> instead of throwing.
/// </summary>
public sealed class NavigationPanelStateJsonConverter : JsonConverter<NavigationPanelState>
{
    public override NavigationPanelState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string? text = reader.GetString();
                return Enum.TryParse<NavigationPanelState>(text, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed)
                    ? parsed
                    : NavigationPanelState.Normal;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int value)
                       && Enum.IsDefined(typeof(NavigationPanelState), value)
                    ? (NavigationPanelState)value
                    : NavigationPanelState.Normal;
            default:
                return NavigationPanelState.Normal;
        }
    }

    public override void Write(Utf8JsonWriter writer, NavigationPanelState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
