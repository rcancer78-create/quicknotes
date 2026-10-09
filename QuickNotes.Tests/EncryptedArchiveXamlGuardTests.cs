using System;
using System.IO;
using QuickNotes.App.Helpers;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public sealed class EncryptedArchiveXamlGuardTests
{
    [Fact]
    public void ImportExportWindowXaml_QnarSetup_UsesPasswordBoxes_AndDoesNotBindSecretsOrClipboard()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.App", "Views", "ImportExportWindow.xaml"));
        Assert.Contains("PasswordBox x:Name=\"EncryptedArchivePasswordBox\"", xaml);
        Assert.Contains("PasswordBox x:Name=\"EncryptedArchivePasswordConfirmBox\"", xaml);
        Assert.Contains("PasswordBox x:Name=\"EncryptedArchiveRecoveryConfirmBox\"", xaml);
        Assert.Contains("CreateEncryptedArchiveCommand", xaml);
        Assert.Contains("VerifyEncryptedArchiveRecoveryCommand", xaml);
        Assert.Contains("CancelEncryptedArchiveSetupCommand", xaml);
        Assert.Contains("LastSuccessfulEncryptedArchiveBanner", xaml);
        Assert.Contains("ShownRecoveryKey, Mode=OneWay", xaml);
        Assert.Contains("RecoveryKeyDoesNotReplaceNotePasswordText", xaml);
        Assert.Contains("RecoveryKeyShowOnceWarningText", xaml);
        Assert.Contains("PasswordBox x:Name=\"EncryptedArchiveRotatePasswordBox\"", xaml);
        Assert.Contains("PasswordBox x:Name=\"EncryptedArchiveRotateRecoveryBox\"", xaml);
        Assert.Contains("SaveRecoveryKeyOfflineCopyCommand", xaml);
        Assert.Contains("PrintRecoveryKeyCommand", xaml);
        Assert.Contains("RotateEncryptedArchiveRecoveryCommand", xaml);
        Assert.Contains("EncryptedArchiveRotatePath", xaml);
        Assert.DoesNotContain("Text=\"{Binding EncryptedArchivePassword", xaml);
        Assert.DoesNotContain("Text=\"{Binding RecoveryKeyConfirmation", xaml);
        Assert.DoesNotContain("Clipboard", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CopyRecovery", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsWindowXaml_ShowsLastSuccessfulQnar_WithoutSecretBindings()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.App", "Views", "SettingsWindow.xaml"));
        Assert.Contains("EncryptedArchiveLastSuccessBanner", xaml);
        Assert.Contains("x:Static h:UserTaskCopy.PortableArchiveHint", xaml);
        Assert.Contains(".qnar", UserTaskCopy.PortableArchiveHint, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding EncryptedArchivePassword", xaml);
        Assert.DoesNotContain("ShownRecoveryKey", xaml);
    }

    [Fact]
    public void SetupCopy_StatesRecoveryKeyDoesNotReplaceNotePassword()
    {
        Assert.Contains("не заменяет пароль защищённой заметки", EncryptedArchiveSetupUi.RecoveryKeyDoesNotReplaceNotePassword, StringComparison.Ordinal);
        Assert.Contains("не копирует его в буфер обмена", EncryptedArchiveSetupUi.RecoveryKeyShowOnceWarning, StringComparison.Ordinal);
        Assert.Contains("контрольное восстановление", EncryptedArchiveSetupUi.SetupIncompleteHint, StringComparison.Ordinal);
        Assert.Contains("Документы", EncryptedArchiveSetupUi.OfflineCopySectionHint, StringComparison.Ordinal);
        Assert.Contains("recovery-wrap", EncryptedArchiveSetupUi.RotateSectionHint, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBehind_DoesNotCopySecretsToClipboard()
    {
        string cs = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.App", "Views", "ImportExportWindow.xaml.cs"));
        Assert.DoesNotContain("Clipboard", cs, StringComparison.Ordinal);
        Assert.Contains("ClearEncryptedArchiveSecrets", cs, StringComparison.Ordinal);
        Assert.Contains("EncryptedArchivePasswordBox.Password = string.Empty", cs, StringComparison.Ordinal);
        Assert.Contains("OnClosing", cs, StringComparison.Ordinal);
        Assert.Contains("IsEncryptedArchiveBusy", cs, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", cs, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportExportWindowXaml_DisablesCloseWhileArchiveBusy()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRoot(), "QuickNotes.App", "Views", "ImportExportWindow.xaml"));
        Assert.Contains("IsEnabled=\"{Binding CanCloseImportExportWindow}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanEditEncryptedArchiveRecoveryInputs}\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanEditEncryptedArchiveRotateInputs}\"", xaml);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuickNotes.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("QuickNotes.sln");
    }
}
