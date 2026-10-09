using System;

namespace QuickNotes.App.Services.Sync;

public interface IDeviceIdProvider
{
    Guid GetDeviceId();
}
