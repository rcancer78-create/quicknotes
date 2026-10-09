using System.Text.Json.Serialization;

namespace QuickNotes.App.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppTheme
{
    Light = 0,
    Dark = 1,
    System = 2,
    Green = 3
}
