using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNotes.App.Models;

/// <summary>
/// A saved local workspace view (spec §7). A view is a named filter query over the
/// existing note set, not a container: notes are never copied into it and it never
/// creates note revisions or Sync packets. Stored only in <see cref="AppSettings"/>
/// as local UI preferences.
/// </summary>
public sealed class SavedWorkspaceView
{
    /// <summary>Stable local identifier (GUID). Missing/corrupt ids get a fresh one.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public NavigationSection Section { get; set; } = NavigationSection.All;

    /// <summary>Tag selected as an inclusive filter (0/1 entries in the first release).</summary>
    public List<int> IncludedTagIds { get; set; } = new();

    /// <summary>Tags excluded by the search query (<c>WITHOUT tag:</c>).</summary>
    public List<int> ExcludedTagIds { get; set; } = new();

    public string SearchQuery { get; set; } = string.Empty;

    public NoteSortMode SortMode { get; set; } = NoteSortMode.Pinned;

    /// <summary>Board or List; Focus is transient and never stored.</summary>
    public WorkspaceViewMode ViewMode { get; set; } = WorkspaceViewMode.List;

    /// <summary>Card density (spec §7.1 "плотность").</summary>
    public bool CompactCards { get; set; }

    public SavedWorkspaceView Clone() => new()
    {
        Id = Id,
        Name = Name,
        Section = Section,
        IncludedTagIds = new List<int>(IncludedTagIds ?? new List<int>()),
        ExcludedTagIds = new List<int>(ExcludedTagIds ?? new List<int>()),
        SearchQuery = SearchQuery,
        SortMode = SortMode,
        ViewMode = ViewMode,
        CompactCards = CompactCards
    };
}

/// <summary>
/// Fail-safe converter for the saved-views list. Missing, null, non-array, unknown,
/// or corrupt entries never throw: unusable entries are skipped and bad individual
/// fields fall back to safe defaults. No note data is ever touched here.
/// </summary>
public sealed class SavedWorkspaceViewsJsonConverter : JsonConverter<List<SavedWorkspaceView>>
{
    public override bool HandleNull => true;

    public override List<SavedWorkspaceView> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<SavedWorkspaceView>();

        if (reader.TokenType == JsonTokenType.Null)
        {
            return result;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            // Consume the unexpected token so the surrounding object can keep deserializing.
            using (JsonDocument.ParseValue(ref reader))
            {
            }
            return result;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var view = ReadView(element);
            if (view != null)
            {
                result.Add(view);
            }
        }

        return result;
    }

    private static SavedWorkspaceView? ReadView(JsonElement element)
    {
        string name = (GetString(element, "Name") ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var view = new SavedWorkspaceView
        {
            Id = GetGuid(element, "Id"),
            Name = name,
            Section = GetEnum(element, "Section", NavigationSection.All),
            IncludedTagIds = GetIntList(element, "IncludedTagIds"),
            ExcludedTagIds = GetIntList(element, "ExcludedTagIds"),
            SearchQuery = GetString(element, "SearchQuery") ?? string.Empty,
            SortMode = GetEnum(element, "SortMode", NoteSortMode.Pinned),
            ViewMode = GetEnum(element, "ViewMode", WorkspaceViewMode.List),
            CompactCards = GetBool(element, "CompactCards", false)
        };

        if (view.ViewMode == WorkspaceViewMode.Focus)
        {
            view.ViewMode = WorkspaceViewMode.List;
        }

        return view;
    }

    public override void Write(Utf8JsonWriter writer, List<SavedWorkspaceView> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value != null)
        {
            foreach (var view in value)
            {
                if (view == null || string.IsNullOrWhiteSpace(view.Name))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("Id", view.Id);
                writer.WriteString("Name", view.Name);
                writer.WriteString("Section", view.Section.ToString());
                WriteIntList(writer, "IncludedTagIds", view.IncludedTagIds);
                WriteIntList(writer, "ExcludedTagIds", view.ExcludedTagIds);
                writer.WriteString("SearchQuery", view.SearchQuery ?? string.Empty);
                writer.WriteString("SortMode", view.SortMode.ToString());
                writer.WriteString(
                    "ViewMode",
                    view.ViewMode == WorkspaceViewMode.Focus ? nameof(WorkspaceViewMode.List) : view.ViewMode.ToString());
                writer.WriteBoolean("CompactCards", view.CompactCards);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteIntList(Utf8JsonWriter writer, string name, List<int>? values)
    {
        writer.WriteStartArray(name);
        if (values != null)
        {
            foreach (int value in values)
            {
                writer.WriteNumberValue(value);
            }
        }
        writer.WriteEndArray();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out int number) => number != 0,
            _ => fallback
        };
    }

    private static TEnum GetEnum<TEnum>(JsonElement element, string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            return Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : fallback;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            && Enum.IsDefined(typeof(TEnum), number))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        return fallback;
    }

    private static List<int> GetIntList(JsonElement element, string name)
    {
        var result = new List<int>();
        if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number))
            {
                result.Add(number);
            }
            else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out int parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static Guid GetGuid(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var id)
            && id != Guid.Empty)
        {
            return id;
        }

        return Guid.NewGuid();
    }
}
