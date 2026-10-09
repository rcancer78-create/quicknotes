using System;
using System.IO;
using System.Linq;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public sealed class UxPackageC08Tests
{
    [Fact]
    public void NoteCard_TitleLeads_ShortDate_HidesQuietCloud_KeepsIdOffPermanentSurface()
    {
        var updated = new DateTime(2026, 9, 15, 14, 5, 0);
        var card = new NoteCardViewModel(new Note
        {
            Id = 42,
            Title = "Приоритетный заголовок",
            Text = "Тело",
            CreatedAt = updated.AddDays(-2),
            UpdatedAt = updated
        });

        Assert.Equal("Приоритетный заголовок", card.DisplayTitle);
        Assert.Equal("#42", card.IdText);
        Assert.Equal("15.09 14:05", card.UpdatedAtShortText);
        Assert.StartsWith("Изменено:", card.UpdatedAtText, StringComparison.Ordinal);
        Assert.True(card.UpdatedAtShortText.Length < card.UpdatedAtText.Length);
        Assert.DoesNotContain("Создано:", card.UpdatedAtShortText, StringComparison.Ordinal);
        Assert.Contains("Создано:", card.HeaderMetaTooltip, StringComparison.Ordinal);
        Assert.Contains("Изменено:", card.HeaderMetaTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("#42", card.UpdatedAtShortText, StringComparison.Ordinal);

        Assert.False(card.ShowPublicationStatusOnCard);
        card.PublicationStatus = LocalCommitSyncStatus.Synchronized;
        Assert.False(card.ShowPublicationStatusOnCard);

        card.PublicationStatus = LocalCommitSyncStatus.PendingUpload;
        Assert.True(card.ShowPublicationStatusOnCard);
        card.PublicationStatus = LocalCommitSyncStatus.Syncing;
        Assert.True(card.ShowPublicationStatusOnCard);
        card.PublicationStatus = LocalCommitSyncStatus.Conflict;
        Assert.True(card.ShowPublicationStatusOnCard);
        card.PublicationStatus = LocalCommitSyncStatus.Error;
        Assert.True(card.ShowPublicationStatusOnCard);
    }

    [Fact]
    public void NoteCard_ProtectedTitleAndDeletedShortDate_StayCompact()
    {
        var deleted = new DateTime(2026, 9, 1, 9, 30, 0);
        var card = new NoteCardViewModel(new Note
        {
            Id = 9,
            Title = "секрет",
            Text = "секретное тело",
            IsProtected = true,
            DeletedAt = deleted
        });

        Assert.Equal("🔒 Защищённая заметка", card.DisplayTitle);
        Assert.Equal("01.09 09:30", card.DeletedAtShortText);
        Assert.StartsWith("Удалено:", card.DeletedAtText, StringComparison.Ordinal);
        Assert.True(card.DeletedAtShortText.Length < card.DeletedAtText.Length);
        Assert.False(card.ShowPublicationStatusOnCard);
    }

    [Fact]
    public void MainWindowCardTemplate_TitleFirst_NoPermanentLocalId_ConditionalCloud()
    {
        string root = FindSolutionRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        int start = xaml.IndexOf("DataType=\"{x:Type vm:NoteCardViewModel}\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = xaml.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string card = xaml.Substring(start, end - start);

        Assert.Contains("DisplayTitle", card, StringComparison.Ordinal);
        Assert.Contains("UpdatedAtShortText", card, StringComparison.Ordinal);
        Assert.Contains("ShowPublicationStatusOnCard", card, StringComparison.Ordinal);
        Assert.Contains("PublicationStatusText", card, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource MutedBrush}\" FontWeight=\"Medium\"", card, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CardQuickActionsPanel\"", card, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\" Grid.ColumnSpan=\"2\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding PublicationStatusIcon", card, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"80\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("IdText", card, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth=\"360\"", card, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatedAtText", card, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturedAtText", card, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding UpdatedAtText", card, StringComparison.Ordinal);
    }

    private static string FindSolutionRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\work\QuickNotes";
    }
}
