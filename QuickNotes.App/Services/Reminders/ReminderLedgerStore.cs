using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// Persistent delivery ledger. Stores only reminder id (hash), status and UTC timestamp.
/// Corrupt files fail closed: treated as empty, never logged as raw contents.
/// </summary>
public sealed class ReminderLedgerStore
{
    public const string FileName = "reminder-ledger.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, ReminderLedgerEntry> _byId = new(StringComparer.Ordinal);

    public ReminderLedgerStore(string filePath)
    {
        _path = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Load();
    }

    public string FilePath => _path;

    public bool IsDelivered(string reminderId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(reminderId, out var entry)
                && entry.Status == ReminderDeliveryStatus.Delivered;
        }
    }

    public void MarkDelivered(string reminderId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(reminderId))
        {
            return;
        }

        lock (_gate)
        {
            _byId[reminderId] = new ReminderLedgerEntry
            {
                Id = reminderId,
                Status = ReminderDeliveryStatus.Delivered,
                UpdatedUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)
            };
            PersistUnlocked();
        }
    }

    public void PruneTo(IReadOnlyCollection<string> liveIds)
    {
        var keep = new HashSet<string>(liveIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        lock (_gate)
        {
            var stale = _byId.Keys.Where(id => !keep.Contains(id)).ToList();
            if (stale.Count == 0)
            {
                return;
            }

            foreach (var id in stale)
            {
                _byId.Remove(id);
            }

            PersistUnlocked();
        }
    }

    public IReadOnlyList<ReminderLedgerEntry> Snapshot()
    {
        lock (_gate)
        {
            return _byId.Values.Select(e => new ReminderLedgerEntry
            {
                Id = e.Id,
                Status = e.Status,
                UpdatedUtc = e.UpdatedUtc
            }).ToList();
        }
    }

    public string ReadRawForTests() => File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;

    private void Load()
    {
        lock (_gate)
        {
            _byId = new Dictionary<string, ReminderLedgerEntry>(StringComparer.Ordinal);
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                var doc = JsonSerializer.Deserialize<ReminderLedgerDocument>(json, JsonOptions);
                if (doc?.Entries == null)
                {
                    return;
                }

                foreach (var entry in doc.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length != 64)
                    {
                        continue;
                    }

                    if (!IsHex(entry.Id))
                    {
                        continue;
                    }

                    _byId[entry.Id] = new ReminderLedgerEntry
                    {
                        Id = entry.Id,
                        Status = ReminderDeliveryStatus.Delivered,
                        UpdatedUtc = entry.UpdatedUtc.Kind == DateTimeKind.Utc
                            ? entry.UpdatedUtc
                            : DateTime.SpecifyKind(entry.UpdatedUtc, DateTimeKind.Utc)
                    };
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _byId.Clear();
                ErrorLogService.Write("ReminderLedger.Load", ex.GetType().Name);
            }
        }
    }

    private void PersistUnlocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var doc = new ReminderLedgerDocument
        {
            Version = 1,
            Entries = _byId.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToList()
        };
        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(doc, JsonOptions));
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
