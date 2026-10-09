using System;
using System.IO;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class SettingsServiceTests
{
    [Fact]
    public void Scenario7_SettingsService_SavesAndLoadsCorrectly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quicknotes-settings-{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(path, _ => { });
        var initial = settingsService.LoadSettings();

        Assert.NotNull(initial);
        Assert.NotNull(initial.HotkeyKey);

        // Update settings
        var updated = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyShift = false,
            HotkeyAlt = true,
            HotkeyWin = false,
            HotkeyKey = "F9",
            InstantHotkeyCtrl = false,
            InstantHotkeyShift = true,
            InstantHotkeyAlt = true,
            InstantHotkeyWin = true,
            InstantHotkeyKey = "F10",
            RunOnStartup = false,
            StartMinimizedToTray = true,
            ShowNotificationOnSave = false
        };

        settingsService.SaveSettings(updated);
        var loaded = settingsService.LoadSettings();

        Assert.True(loaded.HotkeyCtrl);
        Assert.False(loaded.HotkeyShift);
        Assert.True(loaded.HotkeyAlt);
        Assert.False(loaded.HotkeyWin);
        Assert.Equal("F9", loaded.HotkeyKey);
        Assert.False(loaded.InstantHotkeyCtrl);
        Assert.True(loaded.InstantHotkeyShift);
        Assert.True(loaded.InstantHotkeyAlt);
        Assert.True(loaded.InstantHotkeyWin);
        Assert.Equal("F10", loaded.InstantHotkeyKey);
        Assert.True(loaded.StartMinimizedToTray);
        Assert.False(loaded.ShowNotificationOnSave);
        Assert.Equal(260, loaded.TagPanelWidth);

        // Restore defaults
        File.Delete(path);
    }

    [Fact]
    public void LoadSettings_IgnoresLegacyVectorSearchFields_AndOmitsThemOnSave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quicknotes-settings-legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "HotkeyKey": "F8",
              "SemanticSearchEnabled": true,
              "SemanticModelPath": "C:\\\\models\\\\vectors.bin",
              "SemanticMinScore": 0.9,
              "SemanticMaxCandidates": 99,
              "UnknownFutureField": "keep-tolerant"
            }
            """);

        var settingsService = new SettingsService(path, _ => { });
        var loaded = settingsService.CurrentSettings;
        Assert.Equal("F8", loaded.HotkeyKey);

        settingsService.SaveSettings(loaded);
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("SemanticSearchEnabled", saved);
        Assert.DoesNotContain("SemanticModelPath", saved);
        Assert.DoesNotContain("SemanticMinScore", saved);
        Assert.DoesNotContain("SemanticMaxCandidates", saved);
        Assert.Contains("\"HotkeyKey\": \"F8\"", saved);

        File.Delete(path);
    }
}
