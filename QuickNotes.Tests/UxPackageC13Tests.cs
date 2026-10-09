using System;
using System.IO;
using QuickNotes.App.Helpers;
using Xunit;

namespace QuickNotes.Tests;

public sealed class UxPackageC13Tests
{
    [Fact]
    public void UserTaskCopy_NamesFirstLevelAsUserActions()
    {
        Assert.Equal("Копии заметок в облаке", UserTaskCopy.CloudSectionTitle);
        Assert.Equal("Подключить через мастер…", UserTaskCopy.CloudWizardButton);
        Assert.Equal("Хранить копии в облаке", UserTaskCopy.CloudEnableCheckbox);
        Assert.Equal("Дополнительно", UserTaskCopy.CloudAdvancedHeader);
        Assert.Contains("расшифровать", UserTaskCopy.CloudCryptoWarning, StringComparison.Ordinal);
        Assert.Equal("Перенос заметок", UserTaskCopy.TransferWindowTitle);
        Assert.Equal("Сохранить копии", UserTaskCopy.ExportTab);
        Assert.Equal("Загрузить копии", UserTaskCopy.ImportTab);
        Assert.Equal("Архив с паролем", UserTaskCopy.ArchiveTab);
        Assert.Equal("Перенос заметок…", UserTaskCopy.MenuTransfer);
    }

    [Fact]
    public void SettingsAndTransfer_FirstLevelUsesUserTasks_KeepsCryptoAndAdvanced()
    {
        string root = FindSolutionRoot();
        string settings = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "SettingsWindow.xaml"));
        string transfer = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "ImportExportWindow.xaml"));

        Assert.Contains("x:Static h:UserTaskCopy.CloudSectionTitle", settings, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.CloudWizardButton", settings, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloudAdvancedExpander\"", settings, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.CloudCryptoWarning", settings, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.CloudAdvancedHeader", settings, StringComparison.Ordinal);
        Assert.Contains("Access Key ID", settings, StringComparison.Ordinal);
        Assert.Contains("Endpoint", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Синхронизация через Yandex Object Storage", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Включить синхронизацию с Yandex Cloud", settings, StringComparison.Ordinal);

        int expander = settings.IndexOf("x:Name=\"CloudAdvancedExpander\"", StringComparison.Ordinal);
        int accessKey = settings.IndexOf("Access Key ID", StringComparison.Ordinal);
        Assert.True(expander > 0 && accessKey > expander, "S3 access key must stay under Дополнительно.");

        Assert.Contains("x:Static h:UserTaskCopy.TransferWindowTitle", transfer, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.ExportTab", transfer, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.ImportTab", transfer, StringComparison.Ordinal);
        Assert.Contains("x:Static h:UserTaskCopy.ArchiveTab", transfer, StringComparison.Ordinal);
        Assert.Contains("Recovery key", transfer, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Экспорт заметок\"", transfer, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Зашифрованный архив\"", transfer, StringComparison.Ordinal);
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
