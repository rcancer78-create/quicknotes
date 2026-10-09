using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class TaskIndexServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public TaskIndexServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qn_task_idx_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "quicknotes.db");
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
    }

    public void Dispose()
    {
        SqliteTestUtil.TryDeleteDirectory(_dir);
    }

    private TaskIndexService CreateService() => new(() => SqliteTestUtil.CreateContext(_dbPath));

    [Fact]
    public async Task Query_PagingSearchCancellationAndDeletedNotes()
    {
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            for (int i = 0; i < 60; i++)
            {
                db.Notes.Add(new Note
                {
                    Title = "Note " + i,
                    Text = "- [ ] task-" + i.ToString("000") + "\n",
                    UpdatedAt = DateTime.Now.AddMinutes(-i)
                });
            }

            db.Notes.Add(new Note { Title = "Gone", Text = "- [ ] deleted-task", DeletedAt = DateTime.Now });
            db.SaveChanges();
        }

        var service = CreateService();
        var page = await service.QueryAsync(null, skip: 0, take: 50);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal(60, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.DoesNotContain(page.Items, t => t.TaskText.Contains("deleted-task", StringComparison.Ordinal));

        var page2 = await service.QueryAsync(null, skip: 50, take: 50);
        Assert.Equal(10, page2.Items.Count);
        Assert.False(page2.HasMore);

        var search = await service.QueryAsync("task-007", 0, 50);
        Assert.Single(search.Items);
        Assert.Equal("task-007", search.Items[0].TaskText);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.QueryAsync(null, 0, 50, cts.Token));
    }

    [Fact]
    public async Task Query_ProtectedNote_ZeroPlaintextInIndex()
    {
        const string secret = "секретный-checkbox-plaintext";
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            db.Notes.Add(new Note
            {
                Title = string.Empty,
                Text = string.Empty,
                IsProtected = true,
                ProtectedCiphertextBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("- [ ] " + secret))
            });
            db.Notes.Add(new Note { Title = "Open", Text = "- [ ] public-task" });
            db.SaveChanges();
        }

        var page = await CreateService().QueryAsync(secret, 0, 50);
        Assert.Empty(page.Items);
        var all = await CreateService().QueryAsync(null, 0, 50);
        Assert.Single(all.Items);
        Assert.Equal("public-task", all.Items[0].TaskText);
        Assert.DoesNotContain(all.Items, t => t.TaskText.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public void QueryDatedOpenTasks_SkipsUndatedProtectedAndDeleted()
    {
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            db.Notes.Add(new Note { Title = "A", Text = "- [ ] dated @2026-10-01" });
            db.Notes.Add(new Note { Title = "B", Text = "- [ ] undated" });
            db.Notes.Add(new Note { Title = "C", Text = "- [ ] deleted @2026-10-01", DeletedAt = DateTime.Now });
            db.Notes.Add(new Note
            {
                Title = string.Empty,
                Text = string.Empty,
                IsProtected = true,
                ProtectedCiphertextBase64 = "x"
            });
            db.SaveChanges();
        }

        var dated = CreateService().QueryDatedOpenTasks();
        Assert.Single(dated);
        Assert.Equal("dated @2026-10-01", dated[0].TaskText);
    }

    [Fact]
    public async Task Query_DeterministicOrdering_DueDateThenUpdatedThenIdLine()
    {
        var t1 = DateTime.Now.AddHours(-2);
        var t2 = DateTime.Now.AddHours(-1);
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            db.Notes.Add(new Note { Title = "A", Text = "- [ ] later @2026-12-01", UpdatedAt = t2 });
            db.Notes.Add(new Note { Title = "B", Text = "- [ ] sooner @2026-01-01", UpdatedAt = t1 });
            db.Notes.Add(new Note { Title = "C", Text = "- [ ] undated", UpdatedAt = DateTime.Now });
            db.SaveChanges();
        }

        var page = await CreateService().QueryAsync(null, 0, 10);
        Assert.Equal(new[] { "sooner @2026-01-01", "later @2026-12-01", "undated" }, page.Items.Select(i => i.TaskText).ToArray());
    }

    [Fact]
    public async Task Query_DatabaseLargerThanOnePage_DoesNotReturnEntireCorpus()
    {
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            for (int n = 0; n < 40; n++)
            {
                db.Notes.Add(new Note
                {
                    Title = "bulk " + n,
                    Text = "- [ ] a\n- [ ] b\n- [ ] c\n",
                    UpdatedAt = DateTime.Now
                });
            }

            db.SaveChanges();
        }

        var page = await CreateService().QueryAsync(null, 0, TaskIndexService.DefaultPageSize);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal(120, page.TotalCount);
        Assert.True(page.HasMore);
    }
}
