using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class SettingsService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _settingsFilePath;
    private readonly Action<bool> _applyStartup;
    public AppSettings CurrentSettings { get; private set; }
    public string? LoadWarning { get; private set; }

    public SettingsService() : this(Path.Combine(QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory(),
        "settings.json"), ApplyStartupSetting)
    { }

    public SettingsService(string path) : this(path, ApplyStartupSetting) { }

    public SettingsService(string path, Action<bool> applyStartup)
    {
        _settingsFilePath = path;
        _applyStartup = applyStartup;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        CurrentSettings = LoadSettings();
    }

    public AppSettings LoadSettings()
    {
        try
        {
            return File.Exists(_settingsFilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFilePath))
                    ?? throw new JsonException("Пустой файл настроек")
                : new AppSettings { HasCompletedOnboarding = false, CloseToTrayPromptCompleted = false };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = $"Не удалось загрузить настройки. Использованы значения по умолчанию. {ex.Message}";
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        var temp = _settingsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            _applyStartup(settings.RunOnStartup);
            try { File.Move(temp, _settingsFilePath, true); }
            catch (Exception ex)
            {
                ErrorLogService.Write("SettingsService.SaveSettings", ex);
                _applyStartup(CurrentSettings.RunOnStartup);
                throw;
            }
            CurrentSettings = settings;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void ApplyStartupSetting(bool runOnStartup)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryKey, true)
            ?? throw new IOException("Не удалось открыть параметры автозапуска Windows.");
        if (runOnStartup)
        {
            var exe = Environment.ProcessPath ?? throw new IOException("Не удалось определить путь приложения.");
            key.SetValue("QuickNotes", $"\"{exe}\"");
        }
        else key.DeleteValue("QuickNotes", false);
    }
}
