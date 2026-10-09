using System.Text.Json.Serialization;

namespace QuickNotes.App.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NoteSortMode
{
    Pinned = 0,
    CreatedAt = 1,
    UpdatedAt = 2
}
