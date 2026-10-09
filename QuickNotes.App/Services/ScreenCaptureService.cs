using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using QuickNotes.App.Models;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public class ScreenCaptureService : IScreenCaptureService
{
    private readonly Func<IScreenCropOverlay>? _overlayFactory;

    public ScreenCaptureService(Func<IScreenCropOverlay>? overlayFactory = null)
    {
        _overlayFactory = overlayFactory;
    }

    private const int DispatcherPingTimeoutMs = 500;

    public async Task<ScreenCaptureResult> CaptureAreaAsync(CancellationToken cancellationToken = default)
    {
        // When a custom overlay factory is provided (e.g. tests or custom overlay implementation),
        // execute directly without requiring WPF Dispatcher message pumping.
        if (_overlayFactory != null)
        {
            return CaptureAreaCore(cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ScreenCaptureResult.Cancelled();
        }

        // When a WPF Application exists, any Window must be created and shown on the
        // Application's Dispatcher STA thread (foreign STAs FailFast in Window.GetWindowMinMax).
        if (WpfApplication.Current != null)
        {
            var dispatcher = WpfApplication.Current.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return ScreenCaptureResult.Failed("Приложение завершает работу.");
            }

            if (dispatcher.CheckAccess())
            {
                return CaptureAreaCore(cancellationToken);
            }

            // Verify that the Dispatcher is actively pumping messages.
            // If the Dispatcher is not pumping (e.g. headless tests without message loop),
            // do not wait indefinitely or crash on an MTA thread.
            if (!await IsDispatcherPumpingAsync(dispatcher, TimeSpan.FromMilliseconds(DispatcherPingTimeoutMs)).ConfigureAwait(false))
            {
                return ScreenCaptureResult.Failed("Диспетчер пользовательского интерфейса недоступен.");
            }

            try
            {
                var op = dispatcher.InvokeAsync(
                    () => CaptureAreaCore(cancellationToken),
                    DispatcherPriority.Normal,
                    cancellationToken);

                return await op.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ScreenCaptureResult.Cancelled();
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("ScreenCaptureService.InvokeAsync", ex);
                return ScreenCaptureResult.Failed($"Не удалось выполнить захват экрана: {ex.Message}");
            }
        }

        // When Application.Current == null:
        // If current thread is STA, run directly.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return CaptureAreaCore(cancellationToken);
        }

        // Otherwise, current thread is MTA and there is no Application.Current.
        // Run on a dedicated STA thread so Window creation succeeds.
        return await RunOnDedicatedStaThreadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsDispatcherPumpingAsync(Dispatcher dispatcher, TimeSpan timeout)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            var pingOp = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Send);
            var completed = await Task.WhenAny(pingOp.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == pingOp.Task)
            {
                return true;
            }

            pingOp.Abort();
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<ScreenCaptureResult> RunOnDedicatedStaThreadAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ScreenCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(CaptureAreaCore(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(ScreenCaptureResult.Cancelled());
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ScreenCaptureResult.Failed($"Не удалось выполнить захват экрана: {ex.Message}"));
            }
        })
        {
            IsBackground = true,
            Name = "QuickNotes.ScreenCaptureSTA"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return await tcs.Task.ConfigureAwait(false);
    }

    protected virtual ScreenCaptureResult CaptureAreaCore(CancellationToken cancellationToken)
    {
        IScreenCropOverlay? overlay = null;
        CancellationTokenRegistration registration = default;
        try
        {
            overlay = _overlayFactory != null ? _overlayFactory() : new ScreenCropOverlayWindow();

            registration = cancellationToken.Register(() =>
            {
                try
                {
                    CancelOverlay(overlay);
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("ScreenCaptureService.CancelOverlay", ex);
                }
            });

            if (cancellationToken.IsCancellationRequested)
            {
                overlay.RequestCancel();
                overlay.ForceClose();
                return ScreenCaptureResult.Cancelled();
            }

            bool? result;
            try
            {
                result = overlay.ShowModal();
            }
            finally
            {
                overlay.ForceClose();
            }

            if (cancellationToken.IsCancellationRequested || result != true || overlay.SelectedScreenRect == null)
            {
                return ScreenCaptureResult.Cancelled();
            }

            var rect = overlay.SelectedScreenRect.Value;
            if (rect.Width < 8 || rect.Height < 8)
            {
                return ScreenCaptureResult.Cancelled();
            }

            using var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
            }

            Bitmap finalBmp = bmp;
            bool isUpscaled = false;

            // Upscale small selections so Windows OCR has sufficient pixel dimensions
            if (rect.Width < 60 || rect.Height < 40)
            {
                int scale = Math.Max(1, (int)Math.Ceiling(Math.Max(60.0 / rect.Width, 40.0 / rect.Height)));
                int newW = rect.Width * scale;
                int newH = rect.Height * scale;
                var upscaled = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
                using (var gUp = Graphics.FromImage(upscaled))
                {
                    gUp.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    gUp.DrawImage(bmp, 0, 0, newW, newH);
                }
                finalBmp = upscaled;
                isUpscaled = true;
            }

            try
            {
                using var ms = new MemoryStream();
                finalBmp.Save(ms, ImageFormat.Png);
                return ScreenCaptureResult.Succeeded(ms.ToArray(), rect.Width, rect.Height);
            }
            finally
            {
                if (isUpscaled)
                {
                    finalBmp.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ScreenCaptureResult.Cancelled();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("ScreenCaptureService.CaptureArea", ex);
            return ScreenCaptureResult.Failed($"Не удалось выполнить захват экрана: {ex.Message}");
        }
        finally
        {
            registration.Dispose();
            if (overlay != null)
            {
                try
                {
                    overlay.ForceClose();
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("ScreenCaptureService.ForceClose", ex);
                }
            }
        }
    }

    private void CancelOverlay(IScreenCropOverlay overlay)
    {
        void Cancel()
        {
            try
            {
                overlay.RequestCancel();
                overlay.ForceClose();
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("ScreenCaptureService.CancelOverlay.Cancel", ex);
            }
        }

        if (_overlayFactory != null)
        {
            Cancel();
            return;
        }

        if (WpfApplication.Current != null && !WpfApplication.Current.Dispatcher.CheckAccess())
        {
            if (!WpfApplication.Current.Dispatcher.HasShutdownStarted &&
                !WpfApplication.Current.Dispatcher.HasShutdownFinished)
            {
                try
                {
                    WpfApplication.Current.Dispatcher.Invoke(
                        Cancel,
                        System.Windows.Threading.DispatcherPriority.Normal,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(1));
                    return;
                }
                catch
                {
                    // Fall back to direct cancel
                }
            }
        }

        Cancel();
    }
}
