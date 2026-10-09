using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace QuickNotes.Tests;

/// <summary>
/// Owns the testhost WPF Application on one pumping STA dispatcher.
/// Windows are never created on a foreign dedicated STA while Application.Current
/// belongs to a different thread — that path FailFasts in Window.GetWindowMinMax.
/// </summary>
internal static class StaTestHarness
{
    private static readonly object Gate = new();
    private static Dispatcher? _ownedDispatcher;

    public static Application EnsureApplication()
    {
        Dispatcher dispatcher = EnsurePumpingDispatcher();
        if (dispatcher.CheckAccess())
        {
            return Application.Current
                ?? throw new InvalidOperationException("WPF Application was not created on the pumping dispatcher.");
        }

        Application? app = null;
        RunOnDispatcher(dispatcher, () =>
        {
            app = Application.Current;
        }, TimeSpan.FromSeconds(10));
        return app ?? throw new InvalidOperationException("WPF Application was not created on the pumping dispatcher.");
    }

    public static Dispatcher EnsurePumpingDispatcher()
    {
        lock (Gate)
        {
            if (IsAlive(_ownedDispatcher) && Ping(_ownedDispatcher!))
            {
                return _ownedDispatcher!;
            }

            Dispatcher? existing = Application.Current?.Dispatcher;
            if (IsAlive(existing) && Ping(existing!))
            {
                _ownedDispatcher = existing;
                return existing!;
            }

            if (Application.Current != null)
            {
                throw new InvalidOperationException(
                    "Application.Current exists but its dispatcher is not pumping. " +
                    "UI tests must not construct Application on the xUnit thread or shut it down.");
            }
        }

        using var started = new ManualResetEventSlim(false);
        using var pumping = new ManualResetEventSlim(false);
        Exception? startError = null;
        Dispatcher? created = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new QuickNotes.App.App(resourcesOnly: true)
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                created = Dispatcher.CurrentDispatcher;
                started.Set();
                Dispatcher.Run();
                _ = app;
            }
            catch (InvalidOperationException)
            {
                created = Application.Current?.Dispatcher;
                started.Set();
            }
            catch (Exception ex)
            {
                startError = ex;
                started.Set();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Name = "QuickNotes.AppResources";
        thread.Start();

        if (!started.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("WPF application resources failed to load.");
        }

        if (startError != null)
        {
            throw new AggregateException("WPF application resources failed to load", startError);
        }

        if (!IsAlive(created) || created is null)
        {
            throw new InvalidOperationException("WPF application dispatcher is not pumping.");
        }

        try
        {
            created.BeginInvoke(DispatcherPriority.Send, () => pumping.Set());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("WPF application dispatcher is not pumping.", ex);
        }

        if (!pumping.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new InvalidOperationException("WPF application dispatcher is not pumping.");
        }

        lock (Gate)
        {
            _ownedDispatcher = created;
        }

        return created;
    }

    public static void Run(Action action, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        TimeSpan wait = timeout ?? TimeSpan.FromSeconds(25);
        Dispatcher dispatcher = EnsurePumpingDispatcher();
        RunOnDispatcher(dispatcher, action, wait);
    }

    private static bool IsAlive(Dispatcher? dispatcher)
        => dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false };

    private static bool Ping(Dispatcher dispatcher, TimeSpan? timeout = null)
    {
        if (dispatcher.CheckAccess())
        {
            return IsAlive(dispatcher);
        }

        TimeSpan wait = timeout ?? TimeSpan.FromMilliseconds(750);
        using var done = new ManualResetEventSlim(false);
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Send, () => done.Set());
        }
        catch
        {
            return false;
        }

        return done.Wait(wait);
    }

    private static void RunOnDispatcher(Dispatcher dispatcher, Action action, TimeSpan wait)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Exception? error = null;
        using var completed = new ManualResetEventSlim(false);
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        if (!completed.Wait(wait))
        {
            throw new TimeoutException($"STA test action timed out after {wait.TotalSeconds:0}s.");
        }

        if (error != null)
        {
            throw new AggregateException("STA thread error", error);
        }
    }
}
