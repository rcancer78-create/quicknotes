using System;
using System.IO;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public sealed class UxPackageC11Tests
{
    [Fact]
    public void PublicationStatusCopy_FirstLevel_UsesHumanStates_AndFoldsSyncingIntoWaiting()
    {
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, PublicationStatusCopy.FirstLevel(LocalCommitSyncStatus.SavedLocally));
        Assert.Equal(PublicationStatusCopy.WaitingToSend, PublicationStatusCopy.FirstLevel(LocalCommitSyncStatus.PendingUpload));
        Assert.Equal(PublicationStatusCopy.WaitingToSend, PublicationStatusCopy.FirstLevel(LocalCommitSyncStatus.Syncing));
        Assert.Equal(PublicationStatusCopy.InCloud, PublicationStatusCopy.FirstLevel(LocalCommitSyncStatus.Synchronized));
        Assert.Equal(PublicationStatusCopy.Conflict, PublicationStatusCopy.FirstLevel(LocalCommitSyncStatus.Conflict));
        Assert.Equal(PublicationStatusCopy.Error, PublicationStatusCopy.FirstLevel(LocalCommitSyncStatus.Error));

        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, PublicationStatusCopy.FirstLevel(MainSyncStatus.Disabled, false));
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, PublicationStatusCopy.FirstLevel(MainSyncStatus.NeedsConfig, false));
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, PublicationStatusCopy.FirstLevel(MainSyncStatus.Ready, false));
        Assert.Equal(PublicationStatusCopy.InCloud, PublicationStatusCopy.FirstLevel(MainSyncStatus.Ready, true));
        Assert.Equal(PublicationStatusCopy.WaitingToSend, PublicationStatusCopy.FirstLevel(MainSyncStatus.Syncing, true));
        Assert.Equal(PublicationStatusCopy.WaitingToSend, PublicationStatusCopy.FirstLevel(MainSyncStatus.Offline, true));
        Assert.Equal(PublicationStatusCopy.Conflict, PublicationStatusCopy.FirstLevel(MainSyncStatus.Conflicts, true));
        Assert.Equal(PublicationStatusCopy.Error, PublicationStatusCopy.FirstLevel(MainSyncStatus.Error, true));
    }

    [Fact]
    public void NoteCard_FirstLevelCopy_MatchesCatalog_AndKeepsDetailsInTooltip()
    {
        var card = new NoteCardViewModel(new Note { Title = "Статус", Text = "Тело" });
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, card.PublicationStatusText);
        Assert.StartsWith(PublicationStatusCopy.SavedOnThisPc, card.PublicationStatusTooltip, StringComparison.Ordinal);
        Assert.Contains("локальной базе", card.PublicationStatusTooltip, StringComparison.Ordinal);
        Assert.False(card.ShowPublicationStatusOnCard);

        card.PublicationStatus = LocalCommitSyncStatus.PendingUpload;
        Assert.Equal(PublicationStatusCopy.WaitingToSend, card.PublicationStatusText);
        Assert.True(card.ShowPublicationStatusOnCard);

        card.PublicationStatus = LocalCommitSyncStatus.Syncing;
        Assert.Equal(PublicationStatusCopy.WaitingToSend, card.PublicationStatusText);
        Assert.Contains("передача", card.PublicationStatusTooltip, StringComparison.Ordinal);

        card.PublicationStatus = LocalCommitSyncStatus.Synchronized;
        Assert.Equal(PublicationStatusCopy.InCloud, card.PublicationStatusText);
        Assert.False(card.ShowPublicationStatusOnCard);

        card.PublicationStatus = LocalCommitSyncStatus.Conflict;
        Assert.Equal(PublicationStatusCopy.Conflict, card.PublicationStatusText);
        Assert.True(card.ShowPublicationStatusOnCard);

        card.PublicationStatus = LocalCommitSyncStatus.Error;
        Assert.Equal(PublicationStatusCopy.Error, card.PublicationStatusText);
        Assert.True(card.ShowPublicationStatusOnCard);
    }

    [Fact]
    public void MainWindow_StatusBarAndCard_UseFirstLevelText_NotTechnicalJargon()
    {
        string root = FindSolutionRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"PublicationStatusBarButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding SyncStatusActionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{Binding SyncStatusActionAutomationName}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<MouseBinding MouseAction=\"LeftClick\" Command=\"{Binding SyncNowCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PublicationStatusBarText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SyncStatusSummaryText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"{Binding SyncStatusTooltip}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding PublicationStatusText}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Облако: Отключено", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Облако: Синхронизировано", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Сохранено локально", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Конфликт синхронизации", xaml, StringComparison.Ordinal);
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
