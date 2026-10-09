using System;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class ScreenOcrCoordinator : IScreenOcrCoordinator
{
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IOcrService _ocrService;
    private readonly IOcrWindowVisibilityCoordinator _windowVisibilityCoordinator;

    public bool LastWasMainWindowVisible { get; private set; } = true;

    public ScreenOcrCoordinator(
        IScreenCaptureService screenCaptureService,
        IOcrService ocrService,
        IOcrWindowVisibilityCoordinator? windowVisibilityCoordinator = null)
    {
        _screenCaptureService = screenCaptureService ?? throw new ArgumentNullException(nameof(screenCaptureService));
        _ocrService = ocrService ?? throw new ArgumentNullException(nameof(ocrService));
        _windowVisibilityCoordinator = windowVisibilityCoordinator ?? new OcrWindowVisibilityCoordinator();
    }

    public async Task<ScreenOcrResult> ExecuteOcrWorkflowAsync(
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? recognizeTimeout = null)
    {
        // Capture the caller's synchronization context before the first await. The workflow
        // keeps using ConfigureAwait(false) so it never drags a UI thread across capture/OCR
        // I/O, but progress callbacks (WPF bound properties) must still run on the originating
        // thread. With no context captured, headless callers keep plain inline invocation.
        var progress = new UiProgressRelay(onProgress);

        if (cancellationToken.IsCancellationRequested)
        {
            return ScreenOcrResult.Cancelled();
        }

        using var windowScope = _windowVisibilityCoordinator.HideQuickNotesWindows();
        LastWasMainWindowVisible = windowScope.WasMainWindowVisible;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress.Report("Ожидание выделения области экрана...");

            await windowScope.WaitForDesktopCompositionAsync(cancellationToken).ConfigureAwait(false);

            var captureResult = await _screenCaptureService.CaptureAreaAsync(cancellationToken).ConfigureAwait(false);
            if (captureResult.IsCancelled)
            {
                windowScope.RestoreWindows();
                return ScreenOcrResult.Cancelled();
            }

            if (!captureResult.Success)
            {
                windowScope.RestoreWindows();
                return ScreenOcrResult.Failed(captureResult.ErrorMessage ?? "Не удалось выполнить захват экрана.");
            }

            if (captureResult.ImageBytes == null || captureResult.ImageBytes.Length == 0)
            {
                windowScope.RestoreWindows();
                return ScreenOcrResult.Failed("Получено пустое изображение области экрана.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            progress.Report("Распознавание текста...");

            TimeSpan recognizeBound = recognizeTimeout ?? BoundedOperation.OcrRecognizeTimeout;
            var bounded = await BoundedOperation.RunAsync(
                    ct => _ocrService.RecognizeTextAsync(captureResult.ImageBytes, ct),
                    recognizeBound,
                    cancellationToken,
                    "ScreenOcrCoordinator.Recognize",
                    BoundedTimeoutBehavior.AbandonAndObserve)
                .ConfigureAwait(false);

            if (bounded.Outcome == BoundedOperationOutcome.Cancelled)
            {
                windowScope.RestoreWindows();
                return ScreenOcrResult.Cancelled();
            }

            if (bounded.Outcome == BoundedOperationOutcome.Timeout)
            {
                windowScope.RestoreWindows();
                return ScreenOcrResult.TimedOut();
            }

            if (bounded.Outcome != BoundedOperationOutcome.Success || bounded.Value == null)
            {
                windowScope.RestoreWindows();
                return ScreenOcrResult.Failed(bounded.UserMessage);
            }

            OcrResult ocrResult = bounded.Value;

            windowScope.RestoreWindows();

            if (!ocrResult.Success)
            {
                if (string.IsNullOrEmpty(ocrResult.Text) && ocrResult.ErrorMessage != null &&
                    ocrResult.ErrorMessage.Contains("не обнаружен", StringComparison.OrdinalIgnoreCase))
                {
                    return ScreenOcrResult.Empty(ocrResult.ErrorMessage);
                }

                return ScreenOcrResult.Failed(ocrResult.ErrorMessage ?? "Не удалось распознать текст на изображении.");
            }

            if (string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                return ScreenOcrResult.Empty(ocrResult.ErrorMessage ?? "Текст на выбранной области не обнаружен.");
            }

            return ScreenOcrResult.Succeeded(ocrResult.Text, ocrResult.LanguageTag, captureResult.ImageBytes);
        }
        catch (OperationCanceledException)
        {
            windowScope.RestoreWindows();
            return ScreenOcrResult.Cancelled();
        }
        catch (Exception ex)
        {
            windowScope.RestoreWindows();
            ErrorLogService.Write("ScreenOcrCoordinator.Workflow", ex);
            return ScreenOcrResult.Failed(UserFacingOperationError.GenericFailure);
        }
    }

    /// <summary>
    /// Delivers workflow progress on the synchronization context that started the workflow
    /// (the WPF dispatcher for UI callers), so bound properties are never mutated from a
    /// pool-thread continuation. Invocation stays inline when no context was captured or the
    /// caller is already on it, keeping headless/non-UI callers unchanged. Callback exceptions
    /// are not swallowed.
    /// </summary>
    private sealed class UiProgressRelay
    {
        private readonly Action<string>? _callback;
        private readonly SynchronizationContext? _context;

        public UiProgressRelay(Action<string>? callback)
        {
            _callback = callback;
            _context = callback == null ? null : SynchronizationContext.Current;
        }

        public void Report(string message)
        {
            Action<string>? callback = _callback;
            if (callback == null)
            {
                return;
            }

            SynchronizationContext? context = _context;
            if (context == null || ReferenceEquals(SynchronizationContext.Current, context))
            {
                callback(message);
                return;
            }

            context.Send(_ => callback(message), null);
        }
    }
}
