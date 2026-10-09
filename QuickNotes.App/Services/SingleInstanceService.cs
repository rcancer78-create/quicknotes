using System;
using System.Diagnostics;
using System.Threading;

namespace QuickNotes.App.Services;

public class SingleInstanceService : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _waitHandleRegistration;
    private readonly bool _isPrimary;
    private bool _disposed;

    public bool IsPrimary => _isPrimary;

    public SingleInstanceService(string? instanceId = null)
    {
        string id = instanceId ?? Environment.UserName;
        string mutexName = $"Local\\QuickNotes_Mutex_{id}";
        string eventName = $"Local\\QuickNotes_Activate_{id}";

        bool createdNew = false;
        try
        {
            _mutex = new Mutex(true, mutexName, out createdNew);
            _isPrimary = createdNew;
        }
        catch (AbandonedMutexException ex)
        {
            // Previous instance crashed or terminated abnormally without releasing the mutex.
            // The OS has granted ownership to this process, so we are now primary.
            System.Diagnostics.Debug.WriteLine($"SingleInstance: AbandonedMutexException handled: {ex.Message}");
            _isPrimary = true;
        }

        if (_isPrimary)
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName, out _);
            _activateEvent.Reset();
        }
        else
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out var existingEvent))
                {
                    _activateEvent = existingEvent;
                }
                else
                {
                    _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Не удалось открыть событие активации существующего экземпляра: {ex.Message}");
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            }
        }
    }

    public void SignalExistingInstance()
    {
        try
        {
            _activateEvent?.Set();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Не удалось передать сигнал основному экземпляру: {ex.Message}");
        }
    }

    public void StartListening(Action onActivateRequested)
    {
        if (!_isPrimary || _activateEvent == null) return;

        _waitHandleRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (state, timedOut) =>
            {
                if (!timedOut)
                {
                    onActivateRequested();
                }
            },
            null,
            -1,
            false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waitHandleRegistration?.Unregister(null);
        _activateEvent?.Dispose();

        if (_isPrimary && _mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                Debug.WriteLine($"Не удалось освободить mutex экземпляра: {ex.Message}");
            }
        }
        _mutex?.Dispose();
        GC.SuppressFinalize(this);
    }
}
