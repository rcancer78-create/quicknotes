using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public static class ImportFolderMapper
{
    public static List<ImportFolderMappingEntry> BuildMapping(string importRoot)
    {
        var entries = new List<ImportFolderMappingEntry>();
        if (string.IsNullOrWhiteSpace(importRoot) || !Directory.Exists(importRoot))
        {
            return entries;
        }

        string rootName = NormalizeTagName(Path.GetFileName(Path.GetFullPath(importRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = "Импорт";
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootName };
        entries.Add(new ImportFolderMappingEntry
        {
            RelativeFolder = ".",
            ProposedTagName = rootName,
            ParentTagName = null,
            Depth = 0,
            IsAmbiguous = false,
            Role = "Корневой тег (блокнот)"
        });

        var sections = Directory.GetDirectories(importRoot)
            .Select(d => Path.GetFileName(d) ?? string.Empty)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var section in sections)
        {
            string proposed = Deduplicate(NormalizeTagName(section), used);
            entries.Add(new ImportFolderMappingEntry
            {
                RelativeFolder = section,
                ProposedTagName = proposed,
                ParentTagName = rootName,
                Depth = 1,
                IsAmbiguous = false,
                Role = "Дочерний тег (раздел)"
            });

            string sectionFull = Path.Combine(importRoot, section);
            var nested = SafeGetDirectories(sectionFull);
            foreach (var nestedName in nested.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new ImportFolderMappingEntry
                {
                    RelativeFolder = Path.Combine(section, nestedName),
                    ProposedTagName = proposed,
                    ParentTagName = rootName,
                    Depth = 2,
                    IsAmbiguous = true,
                    Role = "Неоднозначная вложенность — не сглаживается молча"
                });
            }
        }

        return entries;
    }

    public static void ApplyMappingToItems(
        IEnumerable<ImportItemPreview> items,
        IReadOnlyList<ImportFolderMappingEntry> mapping,
        string importRoot)
    {
        var root = mapping.FirstOrDefault(m => m.Depth == 0);
        var sectionByFolder = mapping
            .Where(m => m.Depth == 1)
            .ToDictionary(m => m.RelativeFolder, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.SourcePath) || string.IsNullOrWhiteSpace(importRoot))
            {
                continue;
            }

            string relative = GetRelativeUnderRoot(importRoot, item.SourcePath);
            item.SourceRelativePath = relative;
            var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tagNames = new List<string>();
            if (root != null && !string.IsNullOrWhiteSpace(root.ProposedTagName))
            {
                tagNames.Add(root.ProposedTagName.Trim());
            }

            if (parts.Length > 1)
            {
                string section = parts[0];
                if (sectionByFolder.TryGetValue(section, out var map) && !string.IsNullOrWhiteSpace(map.ProposedTagName))
                {
                    tagNames.Add(map.ProposedTagName.Trim());
                }

                if (parts.Length > 2)
                {
                    item.HasAmbiguousFolderMapping = true;
                    item.LostElements.Add("Вложенность глубже «блокнот/раздел» не назначается автоматически.");
                }
            }

            item.ProposedRootTag = tagNames.Count > 0 ? tagNames[0] : string.Empty;
            item.ProposedSectionTag = tagNames.Count > 1 ? tagNames[1] : string.Empty;
            MergeMappedTags(item, tagNames);
        }
    }

    public static string NormalizeTagName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(raw.Length);
        bool lastSpace = false;
        foreach (var ch in raw.Trim())
        {
            if (char.IsControl(ch) || ch == '/' || ch == '\\' || ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|')
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace)
                {
                    builder.Append(' ');
                    lastSpace = true;
                }

                continue;
            }

            lastSpace = false;
            builder.Append(ch);
        }

        string name = builder.ToString().Trim();
        if (name.Length > ImportLimits.MaxTagNameLength)
        {
            name = name[..ImportLimits.MaxTagNameLength].Trim();
        }

        return name;
    }

    private static string Deduplicate(string name, HashSet<string> used)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Раздел";
        }

        string candidate = name;
        int n = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{name} ({n})";
            n++;
        }

        return candidate;
    }

    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path).Select(d => Path.GetFileName(d) ?? string.Empty).Where(s => s.Length > 0);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string GetRelativeUnderRoot(string root, string filePath)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        string fileFull = Path.GetFullPath(filePath);
        if (fileFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return fileFull[rootFull.Length..];
        }

        return Path.GetFileName(filePath);
    }

    private static void MergeMappedTags(ImportItemPreview item, List<string> tagNames)
    {
        var existing = new HashSet<string>(item.Tags.Select(t => t.TagName), StringComparer.OrdinalIgnoreCase);
        foreach (var name in tagNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Add(name))
            {
                item.Tags.Add(new ExportNoteTagDto
                {
                    TagName = name,
                    Origin = TagOrigin.Manual
                });
            }
        }
    }
}
