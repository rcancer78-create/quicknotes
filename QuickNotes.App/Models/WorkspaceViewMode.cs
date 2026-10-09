using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNotes.App.Models;

/// <summary>
/// Central workspace representation for the main window (Release 1.4).
/// <see cref="List"/> is the current three-pane layout and the rollback path.
/// <see cref="Board"/> is the read-only adaptive card board (W2).
/// <see cref="Focus"/> hosts the selected note in the existing detail editor (W3).
/// Focus is a transient presentation state and is never persisted as the restart
/// mode: the settings file continues to store List or Board (the mode to return to).
/// </summary>
[JsonConverter(typeof(WorkspaceViewModeJsonConverter))]
public enum WorkspaceViewMode
{
    List = 0,
    Board = 1,
    Focus = 2
}

/// <summary>
/// Fail-safe JSON converter: missing, unknown, corrupt, or out-of-range values
/// resolve to <see cref="WorkspaceViewMode.List"/> instead of throwing.
/// <see cref="WorkspaceViewMode.Focus"/> is intentionally never restored from disk
/// and also resolves to <see cref="WorkspaceViewMode.List"/>.
/// </summary>
public sealed class WorkspaceViewModeJsonConverter : JsonConverter<WorkspaceViewMode>
{
    public override WorkspaceViewMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string? text = reader.GetString();
                return Enum.TryParse<WorkspaceViewMode>(text, ignoreCase: true, out var parsed)
                       && Enum.IsDefined(parsed)
                       && parsed != WorkspaceViewMode.Focus
                    ? parsed
                    : WorkspaceViewMode.List;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int value)
                       && Enum.IsDefined(typeof(WorkspaceViewMode), value)
                       && value != (int)WorkspaceViewMode.Focus
                    ? (WorkspaceViewMode)value
                    : WorkspaceViewMode.List;
            default:
                return WorkspaceViewMode.List;
        }
    }

    public override void Write(Utf8JsonWriter writer, WorkspaceViewMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == WorkspaceViewMode.Focus
            ? nameof(WorkspaceViewMode.List)
            : value.ToString());
    }
}
