using System;
using System.IO;
using System.Linq;
using System.Windows;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class ReminderSettingsUiTests
{
    [Fact]
    public void KeyboardAccessibleNames_ArePresentInXaml()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ROADMAP.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string settings = File.ReadAllText(Path.Combine(dir!.FullName, "QuickNotes.App", "Views", "SettingsWindow.xaml"));
        Assert.Contains("AutomationProperties.Name=\"Локальные напоминания о задачах с датой\"", settings);
        Assert.Contains("AutomationProperties.Name=\"Статус локальных напоминаний\"", settings);
        Assert.Contains("LocalRemindersEnabledCheckBox", settings);

        string main = File.ReadAllText(Path.Combine(dir.FullName, "QuickNotes.App", "Views", "MainWindow.xaml"));
        Assert.Contains("TaskRemindersStatusText", main);
        Assert.Contains("AutomationProperties.Name=\"Статус локальных напоминаний\"", main);
    }

    [Fact]
    public void SettingsToggle_AndUnavailableStatus_LightDark_WideNarrow()
    {
        StaTestHarness.Run(() =>
        {
            string profile = Path.Combine(Path.GetTempPath(), "qn_rem_ui_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            try
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 0, ownsDirectory: false);
                var vm = fixture.MainViewModel.CreateSettingsViewModel();
                vm.SettingsSectionIndex = 0;
                Assert.True(vm.LocalRemindersEnabled);
                Assert.False(string.IsNullOrWhiteSpace(vm.LocalRemindersStatusText));

                vm.LocalRemindersEnabled = false;
                Assert.Contains("выключены", vm.LocalRemindersStatusText, StringComparison.OrdinalIgnoreCase);

                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.ApplyTheme(theme);
                    foreach (var (w, h) in new[] { (820, 780), (640, 560) })
                    {
                        var window = new SettingsWindow(vm)
                        {
                            Width = w,
                            Height = h
                        };
                        window.Show();
                        ThreePaneUiSmokeRunner.DoEvents();
                        Assert.True(window.LocalRemindersEnabledCheckBox.IsVisible);
                        Assert.True(window.LocalRemindersStatusText.IsVisible);
                        var content = (FrameworkElement)window.Content;
                        var toggleBounds = window.LocalRemindersEnabledCheckBox.TransformToAncestor(content)
                            .TransformBounds(new Rect(0, 0, window.LocalRemindersEnabledCheckBox.ActualWidth, window.LocalRemindersEnabledCheckBox.ActualHeight));
                        Assert.True(toggleBounds.Right <= w + 4, toggleBounds.ToString());
                        window.Close();
                    }
                }
            }
            finally
            {
                SqliteTestUtil.TryDeleteDirectory(profile);
            }
        }, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ToastActivation_OpensUnprotectedNote_ByOpaqueSyncId()
    {
        StaTestHarness.Run(() =>
        {
            string profile = Path.Combine(Path.GetTempPath(), "qn_rem_act_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            try
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 0, ownsDirectory: false);
                var sync = Guid.NewGuid();
                using (var db = fixture.ContextFactory())
                {
                    db.Notes.Add(new QuickNotes.App.Models.Note
                    {
                        Title = "Target",
                        Text = "- [ ] remind me @2026-10-01\n",
                        SyncId = sync
                    });
                    db.SaveChanges();
                }

                var (window, vm) = fixture;
                window.Show();
                vm.OpenNoteFromReminderActivation(sync);
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.NotNull(vm.DetailEditor);
                Assert.Contains("remind me", vm.DetailEditor!.Text, StringComparison.Ordinal);
                window.Close();
            }
            finally
            {
                SqliteTestUtil.TryDeleteDirectory(profile);
            }
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AppSettings_LocalRemindersDefaultOn_AndRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "qn-rem-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new SettingsService(path, _ => { });
            Assert.True(new AppSettings().LocalRemindersEnabled);
            var updated = service.CurrentSettings;
            updated.LocalRemindersEnabled = false;
            service.SaveSettings(updated);
            var loaded = new SettingsService(path, _ => { }).LoadSettings();
            Assert.False(loaded.LocalRemindersEnabled);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
