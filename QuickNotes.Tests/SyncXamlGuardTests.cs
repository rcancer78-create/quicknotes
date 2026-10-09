using System;
using System.IO;
using Xunit;

namespace QuickNotes.Tests;

public class SyncXamlGuardTests
{
    private static string GetProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }
        return @"D:\work\QuickNotes";
    }

    [Fact]
    public void SettingsWindowXaml_GuardsSensitiveInputs_AndRequiresPasswordBoxes()
    {
        var root = GetProjectRoot();
        var xamlPath = Path.Combine(root, "QuickNotes.App", "Views", "SettingsWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"SettingsWindow.xaml not found at {xamlPath}");

        var content = File.ReadAllText(xamlPath);

        // Verify PasswordBoxes are used for sensitive secrets
        Assert.Contains("PasswordBox x:Name=\"SecretAccessKeyBox\"", content);
        Assert.Contains("PasswordBox x:Name=\"EncryptionPasswordBox\"", content);
        Assert.Contains("PasswordBox x:Name=\"EncryptionPasswordConfirmBox\"", content);

        // Verify NO plaintext bindings to secret key or password
        Assert.DoesNotContain("Text=\"{Binding SecretAccessKey", content);
        Assert.DoesNotContain("Text=\"{Binding EncryptionPassword", content);

        // Verify sync commands exist
        Assert.Contains("Command=\"{Binding SaveSyncSettingsCommand}\"", content);
        Assert.Contains("Command=\"{Binding TestConnectionCommand}\"", content);
        Assert.Contains("Command=\"{Binding DeleteSyncCredentialsCommand}\"", content);

        // Verify sync bucket & prefix bindings exist
        Assert.Contains("Text=\"{Binding SyncBucket", content);
        Assert.Contains("Text=\"{Binding SyncPrefix", content);
        Assert.Contains("IsChecked=\"{Binding SyncAttachments}\"", content);
        Assert.Contains("Text=\"{Binding MaxAttachmentSyncMb", content);

        // Verify expander for advanced params
        Assert.Contains("Text=\"{Binding SyncEndpoint", content);
        Assert.Contains("Text=\"{Binding SyncRegion", content);
        Assert.Contains("Text=\"{Binding CloudUsageText", content);
        Assert.Contains("Text=\"{Binding CloudQuotaStatusText", content);
        Assert.Contains("Command=\"{Binding PreviewCloudCleanupCommand}\"", content);
        Assert.Contains("Command=\"{Binding ExecuteCloudCleanupCommand}\"", content);
        Assert.Contains("PasswordBox x:Name=\"RotationOldPasswordBox\"", content);
        Assert.Contains("PasswordBox x:Name=\"RotationNewPasswordBox\"", content);
        Assert.Contains("PasswordBox x:Name=\"RotationNewPasswordConfirmBox\"", content);
        Assert.Contains("Command=\"{Binding PreviewPasswordRotationCommand}\"", content);
        Assert.Contains("Command=\"{Binding ExecutePasswordRotationCommand}\"", content);
        Assert.DoesNotContain("Text=\"{Binding Rotation", content);
    }

    [Fact]
    public void SyncConflictsWindowXaml_HasRequiredElements_AndResolutionCommands()
    {
        var root = GetProjectRoot();
        var xamlPath = Path.Combine(root, "QuickNotes.App", "Views", "SyncConflictsWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"SyncConflictsWindow.xaml not found at {xamlPath}");

        var content = File.ReadAllText(xamlPath);

        // Verify 4 resolution action commands
        Assert.Contains("Command=\"{Binding KeepBothCommand}\"", content);
        Assert.Contains("Command=\"{Binding KeepLocalCommand}\"", content);
        Assert.Contains("Command=\"{Binding AcceptRemoteCommand}\"", content);
        Assert.Contains("Command=\"{Binding MergeCommand}\"", content);

        // Verify refresh and close commands
        Assert.Contains("Command=\"{Binding RefreshCommand}\"", content);
        Assert.Contains("Command=\"{Binding CloseCommand}\"", content);

        // Verify side-by-side elements
        Assert.Contains("Локальная версия", content);
        Assert.Contains("Облачная версия", content);
        Assert.Contains("AutomationProperties.Name=\"Оставить локальную версию\"", content);
        Assert.Contains("MergeEditorBox", content);

        // Verify merged text editor
        Assert.Contains("Text=\"{Binding MergedText", content);

        // Verify corruption warning banner
        Assert.Contains("Не удалось прочитать содержимое версии", content);
    }

    [Fact]
    public void MainWindowXaml_ContainsSyncButtons_AndStatusBarIndicators()
    {
        var root = GetProjectRoot();
        var xamlPath = Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml");
        Assert.True(File.Exists(xamlPath), $"MainWindow.xaml not found at {xamlPath}");

        var content = File.ReadAllText(xamlPath);

        // Verify Top Bar SyncNowCommand and OpenSyncConflictsCommand
        Assert.Contains("Command=\"{Binding SyncNowCommand}\"", content);
        Assert.Contains("Command=\"{Binding OpenSyncConflictsCommand}\"", content);
        Assert.Contains("Content=\"{Binding ConflictBadgeText}\"", content);
        Assert.Contains("HasUnresolvedConflicts", content);

        // Verify Status Bar indicators
        Assert.Contains("Text=\"{Binding SyncStatusSummaryText}\"", content);
        Assert.Contains("x:Name=\"PublicationStatusBarText\"", content);
        Assert.Contains("x:Name=\"PublicationStatusBarButton\"", content);
        Assert.Contains("Command=\"{Binding SyncStatusActionCommand}\"", content);
        Assert.Contains("AutomationProperties.Name=\"{Binding SyncStatusActionAutomationName}\"", content);
        Assert.Contains("ToolTip=\"{Binding SyncStatusTooltip}\"", content);
        Assert.Contains("Binding SyncStatusActionTone", content);
        Assert.DoesNotContain("Binding SyncStatus\"", content);
        Assert.Contains("IsSyncBusy", content);
    }
}
