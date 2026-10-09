using System;
using QuickNotes.App.Services;

namespace QuickNotes.App.Services.Sync;

public class DeviceIdProvider : IDeviceIdProvider
{
    private readonly SettingsService _settingsService;
    private readonly object _lock = new();

    public DeviceIdProvider(SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public Guid GetDeviceId()
    {
        lock (_lock)
        {
            var settings = _settingsService.CurrentSettings;
            if (settings.DeviceId != Guid.Empty)
            {
                return settings.DeviceId;
            }

            var newId = Guid.NewGuid();
            settings.DeviceId = newId;
            _settingsService.SaveSettings(settings);
            return newId;
        }
    }
}

public class FixedDeviceIdProvider : IDeviceIdProvider
{
    private readonly Guid _deviceId;

    public FixedDeviceIdProvider(Guid deviceId)
    {
        _deviceId = deviceId;
    }

    public Guid GetDeviceId() => _deviceId;
}
