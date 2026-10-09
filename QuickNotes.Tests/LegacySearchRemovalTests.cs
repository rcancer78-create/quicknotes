using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class LegacyUnsupportedIndexCleanupTests
{
    [Fact]
    public void TryDeleteKnownIndexFiles_RemovesOnlyKnownIndexAndSidecars()
    {
        string root = Path.Combine(Path.GetTempPath(), "qn-legacy-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Backups"));
        try
        {
            string notes = Path.Combine(root, "quicknotes.db");
            string backup = Path.Combine(root, "Backups", "quicknotes.db");
            string settings = Path.Combine(root, "settings.json");
            string otherDb = Path.Combine(root, "other.db");
            string index = Path.Combine(root, LegacyUnsupportedIndexCleanup.KnownIndexFileName);
            string wal = index + "-wal";
            string shm = index + "-shm";
            string nested = Path.Combine(root, "Backups", LegacyUnsupportedIndexCleanup.KnownIndexFileName);

            File.WriteAllText(notes, "notes");
            File.WriteAllText(backup, "backup");
            File.WriteAllText(settings, "{}");
            File.WriteAllText(otherDb, "other");
            File.WriteAllText(index, "index");
            File.WriteAllText(wal, "wal");
            File.WriteAllText(shm, "shm");
            File.WriteAllText(nested, "nested");

            LegacyUnsupportedIndexCleanup.TryDeleteKnownIndexFiles(root);

            Assert.False(File.Exists(index));
            Assert.False(File.Exists(wal));
            Assert.False(File.Exists(shm));
            Assert.True(File.Exists(notes));
            Assert.True(File.Exists(backup));
            Assert.True(File.Exists(settings));
            Assert.True(File.Exists(otherDb));
            Assert.True(File.Exists(nested), "Must not recurse into Backups or other subfolders");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryDeleteKnownIndexFiles_MissingFile_IsNoOp()
    {
        string root = Path.Combine(Path.GetTempPath(), "qn-legacy-index-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string notes = Path.Combine(root, "quicknotes.db");
            File.WriteAllText(notes, "notes");
            LegacyUnsupportedIndexCleanup.TryDeleteKnownIndexFiles(root);
            Assert.True(File.Exists(notes));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

public sealed class LegacySearchRemovalGuardTests
{
    [Fact]
    public void ProductionSources_DoNotContainRemovedSearchSubsystemIdentifiersOrUiCopy()
    {
        string root = FindRoot();
        string appRoot = Path.Combine(root, "QuickNotes.App");
        string[] forbidden =
        {
            "SemanticSearch",
            "SemanticModel",
            "SemanticIndex",
            "SemanticMinScore",
            "SemanticMaxCandidates",
            "SemanticSearchEnabled",
            "LocalVectorModelEmbedder"
        };

        string[] uiPhrases =
        {
            "смысловой поиск",
            "семантический поиск",
            "по смыслу",
            "смысловое совпадение"
        };

        foreach (string file in Directory.EnumerateFiles(appRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string ext = Path.GetExtension(file);
            if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, text);
            }

            foreach (string phrase in uiPhrases)
            {
                Assert.DoesNotContain(phrase, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void SearchBox_KeepsKeyboardAccessibleName_WithoutModeToggle()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.App", "Views", "MainWindow.xaml"));
        Assert.Contains("AutomationProperties.Name=\"Поиск заметок\"", xaml);
        Assert.Contains("MinWidth=\"640\"", xaml);
        Assert.DoesNotContain("ToggleSemanticSearchCommand", xaml);
        Assert.DoesNotContain("HasSemanticScore", xaml);
    }

    [Fact]
    public void SettingsWindow_HasNoSearchSubsystemSection()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.App", "Views", "SettingsWindow.xaml"));
        Assert.DoesNotContain("Смысловой", xaml);
        Assert.DoesNotContain("ListBoxItem Content=\"Поиск\"", xaml);
        Assert.Contains("ListBoxItem Content=\"Основные\"", xaml);
        Assert.Contains("ListBoxItem Content=\"Облако\"", xaml);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ROADMAP.md")) &&
                File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate QuickNotes solution root");
    }
}
