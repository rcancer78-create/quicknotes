using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public interface ITagRuleStore
{
    TagRule? GetRule(int tagId);
    IReadOnlyDictionary<int, TagRule> GetAllRules();
    void SaveRule(TagRule rule);
    bool DeleteRule(int tagId);
}

public class TagRuleService : ITagRuleStore
{
    private readonly string _filePath;
    private readonly Dictionary<int, TagRule> _rules = new();
    private readonly object _lock = new();

    public string? LoadWarning { get; private set; }

    public TagRuleService() : this(Path.Combine(
        QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory(),
        "tag_rules.json"))
    {
    }

    public TagRuleService(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            _rules.Clear();
            LoadWarning = null;

            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var list = JsonSerializer.Deserialize<List<TagRule>>(json);
                if (list != null)
                {
                    foreach (var rule in list)
                    {
                        rule.Normalize();
                        if (!rule.IsEmpty)
                        {
                            _rules[rule.TagId] = rule;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LoadWarning = $"Не удалось загрузить правила автотегов. {ex.Message}";
                _rules.Clear();
            }
        }
    }

    private void SaveInternal()
    {
        var temp = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var list = _rules.Values.Select(r => r.Clone()).ToList();
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanupEx)
                {
                    System.Diagnostics.Debug.WriteLine($"TagRuleService cleanup failed: {cleanupEx.Message}");
                }
            }
        }
    }

    public TagRule? GetRule(int tagId)
    {
        lock (_lock)
        {
            return _rules.TryGetValue(tagId, out var rule) ? rule.Clone() : null;
        }
    }

    public IReadOnlyDictionary<int, TagRule> GetAllRules()
    {
        lock (_lock)
        {
            return _rules.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone());
        }
    }

    public void SaveRule(TagRule rule)
    {
        if (rule == null)
            return;

        lock (_lock)
        {
            var clean = rule.Clone();
            clean.Normalize();

            if (clean.IsEmpty)
            {
                _rules.Remove(clean.TagId);
            }
            else
            {
                _rules[clean.TagId] = clean;
            }

            SaveInternal();
        }
    }

    public bool DeleteRule(int tagId)
    {
        lock (_lock)
        {
            if (_rules.Remove(tagId))
            {
                SaveInternal();
                return true;
            }

            return false;
        }
    }
}

public class TagRuleStore : TagRuleService
{
    public TagRuleStore() : base() { }
    public TagRuleStore(string filePath) : base(filePath) { }
}
