using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ImportFolderMappingTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public ImportFolderMappingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_map_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db.sqlite");
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
    }

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_root);

    [Fact]
    public void NotebookRootAndImmediateSection_BecomeRootAndChildTags_DeterministicOrder()
    {
        string importRoot = Path.Combine(_root, "МойБлокнот");
        Directory.CreateDirectory(Path.Combine(importRoot, "РазделБ"));
        Directory.CreateDirectory(Path.Combine(importRoot, "РазделА"));
        File.WriteAllText(Path.Combine(importRoot, "root.md"), "# Корневая", Encoding.UTF8);
        File.WriteAllText(Path.Combine(importRoot, "РазделА", "a.md"), "# А", Encoding.UTF8);
        File.WriteAllText(Path.Combine(importRoot, "РазделБ", "b.md"), "# Б", Encoding.UTF8);

        var mapping = ImportFolderMapper.BuildMapping(importRoot);
        Assert.Equal(new[] { ".", "РазделА", "РазделБ" }, mapping.Where(m => !m.IsAmbiguous).Select(m => m.RelativeFolder).ToArray());
        Assert.Equal("МойБлокнот", mapping[0].ProposedTagName);
        Assert.All(mapping.Where(m => m.Depth == 1), m => Assert.Equal("МойБлокнот", m.ParentTagName));

        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var preview = new NoteImportService().BuildPreviewFromDirectory(db, importRoot);
        var a = preview.Items.Single(i => i.SourceFileName == "a.md");
        Assert.Contains(a.Tags, t => t.TagName == "МойБлокнот");
        Assert.Contains(a.Tags, t => t.TagName == "РазделА");
        var rootNote = preview.Items.Single(i => i.SourceFileName == "root.md");
        Assert.Contains(rootNote.Tags, t => t.TagName == "МойБлокнот");
        Assert.DoesNotContain(rootNote.Tags, t => t.TagName == "РазделА");
    }

    [Fact]
    public void DeeperNesting_IsAmbiguous_NotSilentlyFlattened_AndMappingIsEditable()
    {
        string importRoot = Path.Combine(_root, "NB");
        Directory.CreateDirectory(Path.Combine(importRoot, "Sec", "Deep"));
        File.WriteAllText(Path.Combine(importRoot, "Sec", "Deep", "x.md"), "# X", Encoding.UTF8);

        var mapping = ImportFolderMapper.BuildMapping(importRoot);
        Assert.Contains(mapping, m => m.IsAmbiguous && m.RelativeFolder.Contains("Deep", StringComparison.OrdinalIgnoreCase));

        mapping.First(m => m.Depth == 1).ProposedTagName = "Переименованный";
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var preview = new NoteImportService().BuildPreviewFromDirectory(db, importRoot);
        ImportFolderMapper.ApplyMappingToItems(preview.Items, mapping, importRoot);
        var item = Assert.Single(preview.Items);
        Assert.True(item.HasAmbiguousFolderMapping);
        Assert.Contains(item.Tags, t => t.TagName == "Переименованный");
        Assert.Contains(item.LostElements, s => s.Contains("Вложенность", StringComparison.Ordinal));
    }

    [Fact]
    public void TagNameCollisions_GetNumericSuffix()
    {
        var usedStyle = ImportFolderMapper.NormalizeTagName("A/B");
        Assert.Equal("AB", usedStyle);
        string importRoot = Path.Combine(_root, "Dup");
        Directory.CreateDirectory(Path.Combine(importRoot, "Same"));
        Directory.CreateDirectory(Path.Combine(importRoot, "Same "));
        // second folder name after trim/sanitize may collide
        var mapping = ImportFolderMapper.BuildMapping(importRoot);
        var names = mapping.Where(m => m.Depth == 1).Select(m => m.ProposedTagName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
