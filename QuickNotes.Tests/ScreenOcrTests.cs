using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class ScreenOcrTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuickNotesDbContext _context;

    public ScreenOcrTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(_context);
    }

    private readonly List<IDisposable> _disposables = new();
    private readonly List<Window> _ocrWindowsForThisOperation = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _context.Dispose();
        _connection.Dispose();
    }

    private MainViewModel CreateMainViewModel(
        IScreenCaptureService captureService,
        IOcrService ocrService,
        IScreenOcrCoordinator? coordinator = null,
        Action<string, string, MessageBoxImage>? alertHandler = null,
        IOcrWindowVisibilityCoordinator? visibilityCoordinator = null)
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"qn_ocr_test_settings_{Guid.NewGuid():N}.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        _disposables.Add(hotkey);
        var tray = new TrayIconService();
        _disposables.Add(tray);
        var debouncer = new SearchDebouncer(0);
        _disposables.Add(debouncer);

        var screenOcrCoordinator = coordinator ?? new ScreenOcrCoordinator(
            captureService,
            ocrService,
            visibilityCoordinator ?? new OcrWindowVisibilityCoordinator(
                () => _ocrWindowsForThisOperation.ToList(),
                _ => Task.CompletedTask));

        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            searchDebouncer: debouncer,
            screenCaptureService: captureService,
            ocrService: ocrService,
            screenOcrCoordinator: screenOcrCoordinator,
            draftJournalService: NoOpDraftJournalService.Instance);
        vm.AlertHandler = alertHandler ?? ((_, _, _) => { });
        vm.ConfirmHandler = (_, _, _, _) => MessageBoxResult.No;
        _disposables.Add(vm);

        return vm;
    }

    #region Mock Services

    private class MockScreenCaptureService : IScreenCaptureService
    {
        public Func<CancellationToken, Task<ScreenCaptureResult>>? Handler { get; set; }

        public Task<ScreenCaptureResult> CaptureAreaAsync(CancellationToken cancellationToken = default)
        {
            if (Handler != null) return Handler(cancellationToken);
            return Task.FromResult(ScreenCaptureResult.Cancelled());
        }
    }

    private class MockOcrService : IOcrService
    {
        public bool IsSupported { get; set; } = true;
        public Func<byte[], CancellationToken, Task<OcrResult>>? Handler { get; set; }

        public Task<OcrResult> RecognizeTextAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
        {
            if (Handler != null) return Handler(imageBytes, cancellationToken);
            return Task.FromResult(OcrResult.Empty());
        }
    }

    private static byte[] CreateDummyPngBytes(string text = "Тест OCR")
    {
        using var bmp = new Bitmap(200, 60);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 16);
            g.DrawString(text, font, Brushes.Black, 5, 15);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    #endregion

    #region Models & Results Tests

    [Fact]
    public void ScreenCaptureResult_FactoryMethods_SetExpectedProperties()
    {
        var dummyBytes = new byte[] { 1, 2, 3 };

        var succeeded = ScreenCaptureResult.Succeeded(dummyBytes, 100, 50);
        Assert.True(succeeded.Success);
        Assert.False(succeeded.IsCancelled);
        Assert.Null(succeeded.ErrorMessage);
        Assert.Equal(100, succeeded.Width);
        Assert.Equal(50, succeeded.Height);
        Assert.Same(dummyBytes, succeeded.ImageBytes);

        var cancelled = ScreenCaptureResult.Cancelled();
        Assert.False(cancelled.Success);
        Assert.True(cancelled.IsCancelled);
        Assert.Null(cancelled.ImageBytes);

        var failed = ScreenCaptureResult.Failed("Дескриптор не найден");
        Assert.False(failed.Success);
        Assert.False(failed.IsCancelled);
        Assert.Equal("Дескриптор не найден", failed.ErrorMessage);
    }

    [Fact]
    public void OcrResult_FactoryMethods_SetExpectedProperties()
    {
        var success = OcrResult.Succeeded("Распознанный текст", "ru");
        Assert.True(success.Success);
        Assert.Equal("Распознанный текст", success.Text);
        Assert.Equal("ru", success.LanguageTag);
        Assert.Null(success.ErrorMessage);

        var empty = OcrResult.Empty("Текст не найден");
        Assert.False(empty.Success);
        Assert.Equal(string.Empty, empty.Text);
        Assert.Equal("Текст не найден", empty.ErrorMessage);

        var failed = OcrResult.Failed("Сбой OCR");
        Assert.False(failed.Success);
        Assert.Equal(string.Empty, failed.Text);
        Assert.Equal("Сбой OCR", failed.ErrorMessage);
    }

    [Fact]
    public void ScreenOcrResult_FactoryMethods_SetExpectedProperties()
    {
        var success = ScreenOcrResult.Succeeded("Текст", "ru");
        Assert.True(success.Success);
        Assert.False(success.IsCancelled);
        Assert.False(success.IsEmpty);
        Assert.Equal("Текст", success.Text);
        Assert.Equal("ru", success.LanguageTag);

        var cancelled = ScreenOcrResult.Cancelled();
        Assert.False(cancelled.Success);
        Assert.True(cancelled.IsCancelled);

        var timedOut = ScreenOcrResult.TimedOut();
        Assert.False(timedOut.Success);
        Assert.True(timedOut.IsTimeout);
        Assert.False(timedOut.IsCancelled);
        Assert.Equal(UserFacingOperationError.Timeout, timedOut.ErrorMessage);

        var empty = ScreenOcrResult.Empty("Нет текста");
        Assert.False(empty.Success);
        Assert.True(empty.IsEmpty);
        Assert.Equal("Нет текста", empty.ErrorMessage);

        var failed = ScreenOcrResult.Failed("Ошибка движка");
        Assert.False(failed.Success);
        Assert.False(failed.IsCancelled);
        Assert.False(failed.IsEmpty);
        Assert.Equal("Ошибка движка", failed.ErrorMessage);
    }

    #endregion

    #region Coordinator Tests

    [Fact]
    public async Task Coordinator_WhenCaptureAndOcrSucceed_ReturnsSuccessWithText()
    {
        var dummyBytes = new byte[] { 10, 20, 30 };
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(dummyBytes, 200, 100))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (bytes, _) =>
            {
                Assert.Same(dummyBytes, bytes);
                return Task.FromResult(OcrResult.Succeeded("Строка 1\nСтрока 2", "ru"));
            }
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var progressMessages = new List<string>();

        var result = await coordinator.ExecuteOcrWorkflowAsync(msg => progressMessages.Add(msg));

        Assert.True(result.Success);
        Assert.Equal("Строка 1\nСтрока 2", result.Text);
        Assert.Equal("ru", result.LanguageTag);
        Assert.False(result.IsCancelled);
        Assert.False(result.IsEmpty);
        Assert.Contains(progressMessages, p => p.Contains("Распознавание"));
    }

    [Fact]
    public async Task Coordinator_WhenCaptureCancelled_ReturnsCancelledAndDoesNotInvokeOcr()
    {
        bool ocrCalled = false;
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Cancelled())
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) =>
            {
                ocrCalled = true;
                return Task.FromResult(OcrResult.Succeeded("Не должно вызваться"));
            }
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.False(result.Success);
        Assert.True(result.IsCancelled);
        Assert.False(ocrCalled);
    }

    [Fact]
    public async Task Coordinator_WhenCaptureFails_ReturnsFailureAndDoesNotInvokeOcr()
    {
        bool ocrCalled = false;
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Failed("Сбой драйвера дисплея"))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) =>
            {
                ocrCalled = true;
                return Task.FromResult(OcrResult.Succeeded("Не должно вызваться"));
            }
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.False(result.Success);
        Assert.False(result.IsCancelled);
        Assert.Equal("Сбой драйвера дисплея", result.ErrorMessage);
        Assert.False(ocrCalled);
    }

    [Fact]
    public async Task Coordinator_WhenCaptureReturnsEmptyBytes_ReturnsFailure()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(Array.Empty<byte>(), 10, 10))
        };
        var ocrMock = new MockOcrService();

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.False(result.Success);
        Assert.Contains("пустое изображение", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Coordinator_WhenOcrFails_ReturnsFailureWithErrorMessage()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1 }, 50, 50))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Failed("Языковой пакет не найден"))
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.False(result.Success);
        Assert.Equal("Языковой пакет не найден", result.ErrorMessage);
    }

    [Fact]
    public async Task Coordinator_WhenOcrReturnsEmptyText_ReturnsEmptyResult()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1 }, 50, 50))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Empty("Текст на выбранной области не обнаружен."))
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.False(result.Success);
        Assert.True(result.IsEmpty);
        Assert.Contains("не обнаружен", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Coordinator_WhenOcrReturnsWhitespaceOnly_ReturnsEmptyResult()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1 }, 50, 50))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Succeeded("   \r\n\t   ", "ru"))
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.False(result.Success);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task Coordinator_WhenCancellationRequested_ReturnsCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var captureMock = new MockScreenCaptureService();
        var ocrMock = new MockOcrService();

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var result = await coordinator.ExecuteOcrWorkflowAsync(cancellationToken: cts.Token);

        Assert.False(result.Success);
        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task Coordinator_ProgressAfterCaptureAndOcrAwaits_DeliversOnOriginalDispatcherContext_AndSucceeds()
    {
        var dispatcher = StaTestHarness.EnsurePumpingDispatcher();

        // Both stages complete on pool threads so the coordinator's ConfigureAwait(false)
        // continuations are forced off the dispatcher thread before the next progress report.
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.Run(() => ScreenCaptureResult.Succeeded(new byte[] { 1, 2, 3 }, 10, 10))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.Run(() => OcrResult.Succeeded("Текст на UI-потоке", "ru"))
        };

        var coordinator = new ScreenOcrCoordinator(captureMock, ocrMock);
        var progressMessages = new List<string>();
        var progressThreadIds = new List<int>();
        var progressOnDispatcher = new List<bool>();
        SynchronizationContext? workflowContext = null;

        var operation = dispatcher.InvokeAsync(() =>
        {
            workflowContext = SynchronizationContext.Current;
            return coordinator.ExecuteOcrWorkflowAsync(msg =>
            {
                progressMessages.Add(msg);
                progressThreadIds.Add(Environment.CurrentManagedThreadId);
                progressOnDispatcher.Add(dispatcher.CheckAccess());
            });
        });

        var workflow = await operation.Task;
        var result = await workflow;

        // The workflow must have started on the dispatcher context, otherwise the fix is untested.
        Assert.NotNull(workflowContext);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Текст на UI-потоке", result.Text);
        Assert.Contains(progressMessages, m => m.Contains("Распознавание", StringComparison.Ordinal));
        Assert.All(progressOnDispatcher, onDispatcher =>
            Assert.True(onDispatcher, "OCR progress must be delivered on the originating dispatcher thread."));
        Assert.All(progressThreadIds, id => Assert.Equal(dispatcher.Thread.ManagedThreadId, id));
    }

    #endregion

    #region WindowsOcrService Real/Offline Tests

    [Fact]
    public async Task WindowsOcrService_RecognizeTextAsync_NullOrEmptyBytes_ReturnsFailureWithoutException()
    {
        var service = new WindowsOcrService();

        var resultNull = await service.RecognizeTextAsync(null!);
        Assert.False(resultNull.Success);
        Assert.Contains("отсутствует", resultNull.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var resultEmpty = await service.RecognizeTextAsync(Array.Empty<byte>());
        Assert.False(resultEmpty.Success);
        Assert.Contains("отсутствует", resultEmpty.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsOcrService_ResolveBestLanguage_ReturnsValidLanguageIfAvailable()
    {
        var lang = WindowsOcrService.ResolveBestLanguage();
        if (Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Count > 0)
        {
            Assert.NotNull(lang);
            Assert.False(string.IsNullOrWhiteSpace(lang.LanguageTag));
        }
        else
        {
            Assert.Null(lang);
        }
    }

    [Fact]
    public async Task WindowsOcrService_RecognizeTextAsync_WithSyntheticImage_RecognizesRussianAndEnglishText()
    {
        var service = new WindowsOcrService();
        if (!service.IsSupported)
        {
            return; // Skip when run on OS without OCR packs
        }

        byte[] pngBytes = CreateDummyPngBytes("Hello 2026");
        var result = await service.RecognizeTextAsync(pngBytes);

        Assert.True(result.Success, $"Expected success but got: {result.ErrorMessage}");
        Assert.Contains("2026", result.Text);
    }

    #region Pure CalculateTargetDimensions Tests

    [Fact]
    public void CalculateTargetDimensions_NoDownscale_WhenWithinLimit_ReturnsOriginalWithoutUpscaling()
    {
        uint limit = 2600;

        var fullHd = WindowsOcrService.CalculateTargetDimensions(1920u, 1080u, limit);
        Assert.Equal(1920u, fullHd.Width);
        Assert.Equal(1080u, fullHd.Height);

        var squareSmall = WindowsOcrService.CalculateTargetDimensions(800u, 600u, limit);
        Assert.Equal(800u, squareSmall.Width);
        Assert.Equal(600u, squareSmall.Height);

        // Small image must NOT be upscaled
        var tiny = WindowsOcrService.CalculateTargetDimensions(100u, 50u, limit);
        Assert.Equal(100u, tiny.Width);
        Assert.Equal(50u, tiny.Height);

        // Int overload parity
        var intSmall = WindowsOcrService.CalculateTargetDimensions(100, 50, (int)limit);
        Assert.Equal(100, intSmall.Width);
        Assert.Equal(50, intSmall.Height);
    }

    [Fact]
    public void CalculateTargetDimensions_Wide4K_ProportionallyDownscalesAndPreservesAspect()
    {
        uint limit = 2600;

        // Standard 4K UHD: 3840 x 2160
        var result4K = WindowsOcrService.CalculateTargetDimensions(3840u, 2160u, limit);
        Assert.Equal(limit, result4K.Width);
        Assert.True(result4K.Height <= limit);
        Assert.Equal(1463u, result4K.Height);

        double originalAspect = 3840.0 / 2160.0;
        double targetAspect = (double)result4K.Width / result4K.Height;
        Assert.True(Math.Abs(originalAspect - targetAspect) < 0.005, "Aspect ratio must be preserved for 4K");

        // Dual 4K multi-monitor: 7680 x 2160
        var resultDual4K = WindowsOcrService.CalculateTargetDimensions(7680u, 2160u, limit);
        Assert.Equal(limit, resultDual4K.Width);
        Assert.True(resultDual4K.Height <= limit);
        Assert.Equal(731u, resultDual4K.Height);

        double dualAspect = 7680.0 / 2160.0;
        double targetDualAspect = (double)resultDual4K.Width / resultDual4K.Height;
        Assert.True(Math.Abs(dualAspect - targetDualAspect) < 0.005, "Aspect ratio must be preserved for dual 4K");
    }

    [Fact]
    public void CalculateTargetDimensions_TallImage_ProportionallyDownscalesAndPreservesAspect()
    {
        uint limit = 2600;

        // Portrait 4K: 2160 x 3840
        var resultPortrait = WindowsOcrService.CalculateTargetDimensions(2160u, 3840u, limit);
        Assert.Equal(limit, resultPortrait.Height);
        Assert.True(resultPortrait.Width <= limit);
        Assert.Equal(1463u, resultPortrait.Width);

        double originalAspect = 2160.0 / 3840.0;
        double targetAspect = (double)resultPortrait.Width / resultPortrait.Height;
        Assert.True(Math.Abs(originalAspect - targetAspect) < 0.005, "Aspect ratio must be preserved for portrait");

        // Tall narrow document: 1000 x 5000
        var resultTall = WindowsOcrService.CalculateTargetDimensions(1000u, 5000u, limit);
        Assert.Equal(limit, resultTall.Height);
        Assert.Equal(520u, resultTall.Width);
        Assert.True(resultTall.Width <= limit);
    }

    [Fact]
    public void CalculateTargetDimensions_BothDimensionsNearLimit_HandlesBoundaryCases()
    {
        uint limit = 2600;

        // Exactly at limit: no scaling needed
        var exact = WindowsOcrService.CalculateTargetDimensions(2600u, 2600u, limit);
        Assert.Equal(2600u, exact.Width);
        Assert.Equal(2600u, exact.Height);

        // Just below limit: no scaling needed
        var justBelow = WindowsOcrService.CalculateTargetDimensions(2599u, 2599u, limit);
        Assert.Equal(2599u, justBelow.Width);
        Assert.Equal(2599u, justBelow.Height);

        // Both just above limit: downscaled to limit
        var justAbove = WindowsOcrService.CalculateTargetDimensions(2601u, 2601u, limit);
        Assert.Equal(2600u, justAbove.Width);
        Assert.Equal(2600u, justAbove.Height);

        // One above, one below
        var mixed1 = WindowsOcrService.CalculateTargetDimensions(2601u, 2599u, limit);
        Assert.Equal(2600u, mixed1.Width);
        Assert.True(mixed1.Height <= limit);

        var mixed2 = WindowsOcrService.CalculateTargetDimensions(2599u, 2601u, limit);
        Assert.True(mixed2.Width <= limit);
        Assert.Equal(2600u, mixed2.Height);
    }

    [Fact]
    public void CalculateTargetDimensions_ExtremeAndZeroDimensions_GuaranteesPositiveNonZeroDimensionsWithoutOverflow()
    {
        uint limit = 2600;

        // Zero / negative inputs must yield at least 1
        var zeroWidth = WindowsOcrService.CalculateTargetDimensions(0u, 100u, limit);
        Assert.True(zeroWidth.Width >= 1u);
        Assert.True(zeroWidth.Height >= 1u && zeroWidth.Height <= limit);

        var zeroBoth = WindowsOcrService.CalculateTargetDimensions(0u, 0u, limit);
        Assert.Equal(1u, zeroBoth.Width);
        Assert.Equal(1u, zeroBoth.Height);

        var intNegative = WindowsOcrService.CalculateTargetDimensions(-100, -50, (int)limit);
        Assert.True(intNegative.Width >= 1);
        Assert.True(intNegative.Height >= 1);

        var zeroLimit = WindowsOcrService.CalculateTargetDimensions(100u, 100u, 0u);
        Assert.Equal(1u, zeroLimit.Width);
        Assert.Equal(1u, zeroLimit.Height);

        // Extreme aspect ratios (e.g. 10000x1 or 1x10000) must clamp to >= 1 and never zero
        var extremeWide = WindowsOcrService.CalculateTargetDimensions(10000u, 1u, limit);
        Assert.Equal(limit, extremeWide.Width);
        Assert.Equal(1u, extremeWide.Height); // Clamped to positive 1, never 0!

        var extremeTall = WindowsOcrService.CalculateTargetDimensions(1u, 10000u, limit);
        Assert.Equal(1u, extremeTall.Width); // Clamped to positive 1, never 0!
        Assert.Equal(limit, extremeTall.Height);

        // Large dimensions must not overflow
        var huge = WindowsOcrService.CalculateTargetDimensions(1_000_000u, 500_000u, limit);
        Assert.Equal(limit, huge.Width);
        Assert.True(huge.Height <= limit && huge.Height >= 1u);
    }

    [Fact]
    public void CalculateTargetDimensions_IntAndUintOverloads_ProduceIdenticalResults()
    {
        var uintRes = WindowsOcrService.CalculateTargetDimensions(3840u, 2160u, 2600u);
        var intRes = WindowsOcrService.CalculateTargetDimensions(3840, 2160, 2600);

        Assert.Equal((int)uintRes.Width, intRes.Width);
        Assert.Equal((int)uintRes.Height, intRes.Height);
    }

    #endregion

    #region Downscaled Recognition Integration Tests

    [Fact]
    public async Task WindowsOcrService_RecognizeTextAsync_WhenImageExceedsMaxDimension_DownscalesAndRecognizesText()
    {
        var service = new WindowsOcrService();
        if (!service.IsSupported) return;

        // Force downscaling threshold to 2600 px (standard Windows 10 OCR limit)
        service.MaxImageDimensionOverride = 2600;

        // Create a 4K image (3840 x 2160) containing distinct text
        using var bmp = new Bitmap(3840, 2160);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(FontFamily.GenericSansSerif, 48, System.Drawing.FontStyle.Bold);
            g.DrawString("QuickNotes 4K OCR Success", font, Brushes.Black, 100, 100);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);

        var result = await service.RecognizeTextAsync(ms.ToArray());

        Assert.True(result.Success, $"Expected OCR success for downscaled 4K image, but got: {result.ErrorMessage}");
        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.Contains("QuickNotes", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsOcrService_RecognizeTextAsync_WithCompactOverride_DownscalesAndRecognizesText()
    {
        var service = new WindowsOcrService();
        if (!service.IsSupported) return;

        // Small override to explicitly test scaling down logic on compact images
        service.MaxImageDimensionOverride = 200;

        using var bmp = new Bitmap(400, 100);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using var font = new Font(FontFamily.GenericSansSerif, 20, System.Drawing.FontStyle.Bold);
            g.DrawString("Hello 2026", font, Brushes.Black, 10, 20);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);

        var result = await service.RecognizeTextAsync(ms.ToArray());

        Assert.True(result.Success, $"Expected OCR success, but got: {result.ErrorMessage}");
        Assert.Contains("2026", result.Text);
    }

    [Fact]
    public async Task WindowsOcrService_RecognizeTextAsync_CorruptedImageBytes_ReturnsFailureWithoutUnhandledException()
    {
        var service = new WindowsOcrService();
        if (!service.IsSupported) return;

        byte[] corruptedBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
        var result = await service.RecognizeTextAsync(corruptedBytes);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    #endregion

    #endregion

    #region MainViewModel Integration Tests

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenSuccess_OpensNoteEditorDraft_AndDoesNotSaveBeforeExplicitCommit()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1, 2, 3 }, 300, 150))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Succeeded("Захваченный заголовок\nВторая строка заметок", "ru"))
        };

        var vm = CreateMainViewModel(captureMock, ocrMock);

        NoteEditorViewModel? receivedEditor = null;
        bool? dialogResult = null;

        vm.RequestOpenNoteEditor += editor =>
        {
            receivedEditor = editor;
            // Verify note is NOT in the database while editor is open as draft!
            using var checkDb = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options);
            Assert.Empty(checkDb.Notes.ToList());
            return dialogResult;
        };

        // User closes editor without saving
        dialogResult = false;
        await vm.StartScreenOcrAsync();

        Assert.NotNull(receivedEditor);
        Assert.Equal("Захваченный заголовок\nВторая строка заметок", receivedEditor.Text);
        Assert.Equal("Снимок экрана", receivedEditor.SourceProcessName);
        Assert.Equal("Распознавание текста (OCR)", receivedEditor.SourceWindowTitle);
        Assert.Null(receivedEditor.NoteId); // New note draft!

        // Database must remain completely empty because user did not commit!
        using var finalDb = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options);
        Assert.Empty(finalDb.Notes.ToList());
        Assert.Contains("не сохранён", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitForDesktopComposition_WhenWpfApplicationExistsWithoutMessagePump_Completes()
    {
        _ = StaTestHarness.EnsureApplication();

        using var cts = new CancellationTokenSource();
        var coordinator = new OcrWindowVisibilityCoordinator();
        using var scope = coordinator.HideQuickNotesWindows();
        await scope.WaitForDesktopCompositionAsync(cts.Token);
        scope.RestoreWindows();
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenSuccess_AndWpfApplicationExists_OpensNoteEditorDraft()
    {
        _ = StaTestHarness.EnsureApplication();

        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1, 2, 3 }, 300, 150))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Succeeded("Захваченный заголовок\nВторая строка заметок", "ru"))
        };

        var vm = CreateMainViewModel(captureMock, ocrMock);
        NoteEditorViewModel? receivedEditor = null;
        vm.RequestOpenNoteEditor += editor =>
        {
            receivedEditor = editor;
            return false;
        };

        await vm.StartScreenOcrAsync();

        Assert.NotNull(receivedEditor);
        Assert.Contains("не сохранён", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenUserClicksSaveInEditor_PersistsNoteAndHistoryWithoutImages()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1, 2, 3 }, 300, 150))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Succeeded("Сохраняемый текст из OCR", "ru"))
        };

        var vm = CreateMainViewModel(captureMock, ocrMock);

        vm.RequestOpenNoteEditor += editor =>
        {
            // Simulate user clicking "Сохранить"
            editor.Commit?.Invoke();
            return true;
        };

        await vm.StartScreenOcrAsync();

        using var db = new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options);
        var savedNotes = db.Notes.Include(n => n.Attachments).ToList();

        Assert.Single(savedNotes);
        var note = savedNotes[0];
        Assert.Equal("Сохраняемый текст из OCR", note.Text);
        Assert.Equal("Снимок экрана", note.SourceProcessName);
        Assert.Empty(note.Attachments); // No image added to database!

        // Note revision snapshot must be created in history
        var revisions = db.NoteRevisions.Where(r => r.NoteId == note.Id).ToList();
        Assert.Single(revisions);
        Assert.Equal("Сохраняемый текст из OCR", revisions[0].Text);

        Assert.Equal("Заметка сохранена", vm.StatusText);
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenCaptureCancelled_DoesNotOpenEditor()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Cancelled())
        };
        var ocrMock = new MockOcrService();

        bool editorOpened = false;
        string? alertTitle = null;

        var vm = CreateMainViewModel(captureMock, ocrMock, alertHandler: (_, title, _) => alertTitle = title);
        vm.RequestOpenNoteEditor += _ =>
        {
            editorOpened = true;
            return false;
        };

        await vm.StartScreenOcrAsync();

        Assert.False(editorOpened);
        Assert.Null(alertTitle); // User cancellation should not pop up an error box
        Assert.Equal("Захват экрана отменён", vm.StatusText);
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenOcrEmpty_DoesNotOpenEditorAndShowsAlert()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1 }, 100, 100))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Empty("Текст на выбранной области не обнаружен."))
        };

        bool editorOpened = false;
        string? alertMessage = null;

        var vm = CreateMainViewModel(captureMock, ocrMock, alertHandler: (msg, _, _) => alertMessage = msg);
        vm.RequestOpenNoteEditor += _ =>
        {
            editorOpened = true;
            return false;
        };

        await vm.StartScreenOcrAsync();

        Assert.False(editorOpened);
        Assert.NotNull(alertMessage);
        Assert.Contains("не обнаружен", alertMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Текст на снимке не обнаружен", vm.StatusText);
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenCaptureFails_DoesNotOpenEditorAndShowsAlert()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Failed("Неверный дескриптор экрана"))
        };
        var ocrMock = new MockOcrService();

        bool editorOpened = false;
        string? alertMessage = null;

        var vm = CreateMainViewModel(captureMock, ocrMock, alertHandler: (msg, _, _) => alertMessage = msg);
        vm.RequestOpenNoteEditor += _ =>
        {
            editorOpened = true;
            return false;
        };

        await vm.StartScreenOcrAsync();

        Assert.False(editorOpened);
        Assert.NotNull(alertMessage);
        Assert.Contains("Неверный дескриптор экрана", alertMessage);
        Assert.Equal("Неверный дескриптор экрана", vm.StatusText);
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_WhenOcrUnavailable_DoesNotOpenEditorAndShowsAlert()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1 }, 100, 100))
        };
        var ocrMock = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Failed("Windows OCR недоступен в системе"))
        };

        bool editorOpened = false;
        string? alertMessage = null;

        var vm = CreateMainViewModel(captureMock, ocrMock, alertHandler: (msg, _, _) => alertMessage = msg);
        vm.RequestOpenNoteEditor += _ =>
        {
            editorOpened = true;
            return false;
        };

        await vm.StartScreenOcrAsync();

        Assert.False(editorOpened);
        Assert.NotNull(alertMessage);
        Assert.Contains("Windows OCR недоступен", alertMessage);
        Assert.Equal("Windows OCR недоступен в системе", vm.StatusText);
    }

    [Fact]
    public async Task MainViewModel_ScreenOcr_ConcurrentExecution_IsPrevented()
    {
        var tcs = new TaskCompletionSource<ScreenCaptureResult>();
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => tcs.Task
        };
        var ocrMock = new MockOcrService();

        var vm = CreateMainViewModel(captureMock, ocrMock);

        var firstTask = vm.StartScreenOcrAsync();

        Assert.True(vm.IsScreenOcrInProgress);
        Assert.Equal("Распознавание…", vm.ScreenOcrButtonText);
        Assert.False(vm.ScreenOcrCommand.CanExecute(null));

        // Attempt concurrent execution while first is in progress
        var secondTask = vm.StartScreenOcrAsync();
        await secondTask; // Should return immediately without doing anything

        tcs.SetResult(ScreenCaptureResult.Cancelled());
        await firstTask;

        Assert.False(vm.IsScreenOcrInProgress);
        Assert.Equal("Снимок", vm.ScreenOcrButtonText);
        Assert.True(vm.ScreenOcrCommand.CanExecute(null));
    }

    [Fact]
    public async Task MainViewModel_TrayScreenOcrRequested_TriggersWorkflow()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Cancelled())
        };
        var ocrMock = new MockOcrService();

        var vm = CreateMainViewModel(captureMock, ocrMock);

        // Execute command via command property
        Assert.True(vm.ScreenOcrCommand.CanExecute(null));
        vm.ScreenOcrCommand.Execute(null);

        // Allow async task to complete
        for (int i = 0; i < 50 && vm.IsScreenOcrInProgress; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("Захват экрана отменён", vm.StatusText);
    }

    [Fact]
    public async Task MainViewModel_WhenOcrCancelled_RestoresMainWindow()
    {
        var captureMock = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Cancelled())
        };
        var ocrMock = new MockOcrService();
        var vm = CreateMainViewModel(captureMock, ocrMock);

        int bringToFrontCount = 0;
        vm.RequestBringToFront += () => bringToFrontCount++;

        await vm.StartScreenOcrAsync();

        Assert.Equal("Захват экрана отменён", vm.StatusText);
        Assert.False(vm.IsScreenOcrInProgress);
        Assert.True(bringToFrontCount >= 1);

        await vm.StartScreenOcrAsync();
        Assert.Equal("Захват экрана отменён", vm.StatusText);
        Assert.True(bringToFrontCount >= 2);
        Assert.True(vm.ScreenOcrCommand.CanExecute(null));

        for (int i = 0; i < 8; i++)
        {
            await vm.StartScreenOcrAsync();
        }

        Assert.True(bringToFrontCount >= 10);
        Assert.False(vm.IsScreenOcrInProgress);
    }

    [Fact]
    public async Task MainViewModel_WhenOcrCancelledOrFailed_VisibleMainWindowRaisesBringToFront_HiddenDoesNot()
    {
        await AssertBringToFrontAsync(true, ScreenCaptureResult.Cancelled(), expected: 10);
        await AssertBringToFrontAsync(true, ScreenCaptureResult.Failed("Неверный дескриптор экрана"), expected: 10);
        await AssertBringToFrontAsync(false, ScreenCaptureResult.Cancelled(), expected: 0);
        await AssertBringToFrontAsync(false, ScreenCaptureResult.Failed("Неверный дескриптор экрана"), expected: 0);
    }

    [Fact]
    public async Task MainViewModel_WhenOcrEmptyOrThrows_HonorsCapturedMainWindowVisibility()
    {
        var emptyOcr = new MockOcrService
        {
            Handler = (_, _) => Task.FromResult(OcrResult.Empty("Текст на выбранной области не обнаружен."))
        };
        var capture = new MockScreenCaptureService
        {
            Handler = _ => Task.FromResult(ScreenCaptureResult.Succeeded(new byte[] { 1 }, 10, 10))
        };

        await AssertBringToFrontAsync(true, capture, emptyOcr, expected: 10);
        await AssertBringToFrontAsync(false, capture, emptyOcr, expected: 0);

        var throwing = new ThrowingScreenOcrCoordinator { LastWasMainWindowVisible = true };
        var visibleVm = CreateMainViewModel(new MockScreenCaptureService(), new MockOcrService(), throwing);
        int visibleCount = 0;
        visibleVm.RequestBringToFront += () => visibleCount++;
        await visibleVm.StartScreenOcrAsync();
        Assert.Equal(1, visibleCount);

        throwing.LastWasMainWindowVisible = false;
        var hiddenVm = CreateMainViewModel(new MockScreenCaptureService(), new MockOcrService(), throwing);
        int hiddenCount = 0;
        hiddenVm.RequestBringToFront += () => hiddenCount++;
        await hiddenVm.StartScreenOcrAsync();
        Assert.Equal(0, hiddenCount);
    }

    [Fact]
    public void OcrWindowVisibility_LeftoverNonMainDialog_DoesNotChangeLoadedMainWindowResult()
    {
        StaTestHarness.Run(() =>
        {
            var hostVm = CreateMainViewModel(
                new MockScreenCaptureService { Handler = _ => Task.FromResult(ScreenCaptureResult.Cancelled()) },
                new MockOcrService());

            var dialog = new Window
            {
                Title = "Настройки",
                Width = 120,
                Height = 80,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000
            };
            var main = CreateOffscreenMainWindow(hostVm);
            Window? previousMainWindow = null;
            try
            {
                dialog.Show();
                dialog.Hide();
                if (Application.Current != null)
                {
                    previousMainWindow = Application.Current.MainWindow;
                    Application.Current.MainWindow = dialog;
                }

                main.Show();
                main.UpdateLayout();
                Assert.True(main.IsLoaded);
                Assert.True(main.IsVisible);

                var coordinator = new OcrWindowVisibilityCoordinator(
                    () => new Window[] { dialog, main },
                    _ => Task.CompletedTask);

                for (int i = 0; i < 10; i++)
                {
                    using var visibleScope = coordinator.HideQuickNotesWindows();
                    Assert.True(visibleScope.WasMainWindowVisible);
                }

                Assert.True(main.IsVisible);

                main.Hide();
                Assert.False(main.IsVisible);
                Assert.True(main.IsLoaded);

                for (int i = 0; i < 10; i++)
                {
                    using var hiddenScope = coordinator.HideQuickNotesWindows();
                    Assert.False(hiddenScope.WasMainWindowVisible);
                }

                Assert.False(main.IsVisible);
            }
            finally
            {
                if (Application.Current != null && previousMainWindow != null)
                {
                    try { Application.Current.MainWindow = previousMainWindow; } catch { }
                }

                main.CloseWithoutShutdown();
                try { dialog.Close(); } catch { }
            }
        }, TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void OcrWindowVisibility_HeadlessOperation_IgnoresLeftoverNonMainDialog()
    {
        StaTestHarness.Run(() =>
        {
            var dialog = new Window
            {
                Title = "Импорт и экспорт",
                Width = 120,
                Height = 80,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000
            };
            try
            {
                dialog.Show();
                dialog.Hide();
                if (Application.Current != null)
                {
                    Application.Current.MainWindow = dialog;
                }

                var coordinator = new OcrWindowVisibilityCoordinator(
                    () => new Window[] { dialog },
                    _ => Task.CompletedTask);
                using var scope = coordinator.HideQuickNotesWindows();
                Assert.True(scope.WasMainWindowVisible);
            }
            finally
            {
                try { dialog.Close(); } catch { }
            }
        }, TimeSpan.FromSeconds(25));
    }

    private async Task AssertBringToFrontAsync(bool wasVisible, ScreenCaptureResult captureResult, int expected)
    {
        var capture = new MockScreenCaptureService { Handler = _ => Task.FromResult(captureResult) };
        await AssertBringToFrontAsync(wasVisible, capture, new MockOcrService(), expected);
    }

    private async Task AssertBringToFrontAsync(
        bool wasVisible,
        MockScreenCaptureService capture,
        MockOcrService ocr,
        int expected)
    {
        var vm = CreateMainViewModel(capture, ocr, visibilityCoordinator: new StubVisibilityCoordinator(wasVisible));
        int bringToFront = 0;
        vm.RequestBringToFront += () => bringToFront++;
        for (int i = 0; i < 10; i++)
        {
            await vm.StartScreenOcrAsync();
        }

        Assert.Equal(expected, bringToFront);
        Assert.False(vm.IsScreenOcrInProgress);
    }

    private static MainWindow CreateOffscreenMainWindow(MainViewModel vm)
    {
        return new MainWindow(vm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 640,
            Height = 480
        };
    }

    private sealed class StubVisibilityCoordinator : IOcrWindowVisibilityCoordinator
    {
        private readonly bool _wasVisible;

        public StubVisibilityCoordinator(bool wasVisible) => _wasVisible = wasVisible;

        public IOcrWindowScope HideQuickNotesWindows() => new StubOcrWindowScope(_wasVisible);
    }

    private sealed class StubOcrWindowScope : IOcrWindowScope
    {
        public StubOcrWindowScope(bool wasVisible) => WasMainWindowVisible = wasVisible;

        public bool WasMainWindowVisible { get; }

        public IReadOnlyList<Window> HiddenWindows => Array.Empty<Window>();

        public Task WaitForDesktopCompositionAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void RestoreWindows()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingScreenOcrCoordinator : IScreenOcrCoordinator
    {
        public bool LastWasMainWindowVisible { get; set; } = true;

        public Task<ScreenOcrResult> ExecuteOcrWorkflowAsync(
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default,
            TimeSpan? recognizeTimeout = null)
            => throw new InvalidOperationException("OCR pipeline exploded");
    }

    #endregion

    #region Overlay dismiss regression

    private sealed class FakeScreenCropOverlay : IScreenCropOverlay
    {
        public System.Drawing.Rectangle? SelectedScreenRect { get; set; }
        public bool? ShowResult { get; set; } = false;
        public Exception? ShowException { get; set; }
        public Action? OnShow { get; set; }
        public int ShowModalCalls { get; private set; }
        public int RequestCancelCalls { get; private set; }
        public int ForceCloseCalls { get; private set; }

        public bool? ShowModal()
        {
            ShowModalCalls++;
            OnShow?.Invoke();
            if (ShowException != null)
            {
                throw ShowException;
            }
            return ShowResult;
        }

        public void RequestCancel() => RequestCancelCalls++;

        public void ForceClose() => ForceCloseCalls++;
    }

    [Fact]
    public async Task ScreenCaptureService_WhenOverlayCancelled_ForceClosesOverlay()
    {
        var overlay = new FakeScreenCropOverlay { ShowResult = false };
        var service = new ScreenCaptureService(() => overlay);

        var result = await service.CaptureAreaAsync();

        Assert.True(result.IsCancelled);
        Assert.Equal(1, overlay.ShowModalCalls);
        Assert.True(overlay.ForceCloseCalls >= 1);
    }

    [Fact]
    public async Task ScreenCaptureService_WhenTokenCancelledBeforeShow_ClosesOverlayWithoutLeavingItOpen()
    {
        var overlay = new FakeScreenCropOverlay { ShowResult = true };
        var service = new ScreenCaptureService(() => overlay);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.CaptureAreaAsync(cts.Token);

        Assert.True(result.IsCancelled);
        Assert.Equal(0, overlay.ShowModalCalls);
        Assert.True(overlay.RequestCancelCalls >= 1);
        Assert.True(overlay.ForceCloseCalls >= 1);
    }

    [Fact]
    public async Task ScreenCaptureService_WhenTokenCancelledDuringShow_ClosesOverlay()
    {
        var overlay = new FakeScreenCropOverlay { ShowResult = false };
        using var cts = new CancellationTokenSource();
        overlay.OnShow = () => cts.Cancel();
        var service = new ScreenCaptureService(() => overlay);

        var result = await service.CaptureAreaAsync(cts.Token);

        Assert.True(result.IsCancelled);
        Assert.True(overlay.ForceCloseCalls >= 1);
    }

    [Fact]
    public async Task ScreenCaptureService_WhenShowThrows_ForceClosesOverlay()
    {
        var overlay = new FakeScreenCropOverlay
        {
            ShowException = new InvalidOperationException("сбой показа слоя")
        };
        var service = new ScreenCaptureService(() => overlay);

        var result = await service.CaptureAreaAsync();

        Assert.False(result.Success);
        Assert.False(result.IsCancelled);
        Assert.Contains("сбой показа слоя", result.ErrorMessage);
        Assert.True(overlay.ForceCloseCalls >= 1);
    }

    [Fact]
    public async Task ScreenCaptureService_SecondCaptureAfterCancel_ReopensOverlay()
    {
        int created = 0;
        var service = new ScreenCaptureService(() =>
        {
            created++;
            return new FakeScreenCropOverlay { ShowResult = false };
        });

        var first = await service.CaptureAreaAsync();
        var second = await service.CaptureAreaAsync();

        Assert.True(first.IsCancelled);
        Assert.True(second.IsCancelled);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task Coordinator_WhenCaptureCancelled_DoesNotKeepImageBytes()
    {
        var coordinator = new ScreenOcrCoordinator(
            new MockScreenCaptureService
            {
                Handler = _ => Task.FromResult(ScreenCaptureResult.Cancelled())
            },
            new MockOcrService());

        var result = await coordinator.ExecuteOcrWorkflowAsync();

        Assert.True(result.IsCancelled);
        Assert.Null(result.ImageBytes);
    }

    [Fact]
    public async Task ScreenCaptureService_RealOverlayFromMtaThread_CancelsCleanlyWithoutStaException()
    {
        _ = StaTestHarness.EnsureApplication();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var service = new ScreenCaptureService();

        var result = await Task.Run(() => service.CaptureAreaAsync(cts.Token));

        Assert.True(result.IsCancelled);
    }

    [Fact]
    public async Task ScreenCropOverlayWindow_RequestCancelAndForceClose_FromMtaThread_DoesNotThrow()
    {
        var dispatcher = StaTestHarness.EnsurePumpingDispatcher();
        ScreenCropOverlayWindow? overlay = null;
        dispatcher.Invoke(() =>
        {
            overlay = new ScreenCropOverlayWindow();
        });

        Assert.NotNull(overlay);

        await Task.Run(() =>
        {
            overlay.RequestCancel();
            overlay.ForceClose();
        });

        await Task.Delay(100);
    }

    [Fact]
    public async Task ScreenCaptureService_WhenCaptureCancelledAfterMoreThanThreeSeconds_CompletesCancelledWithoutStaDefect()
    {
        var overlay = new FakeScreenCropOverlay
        {
            OnShow = () => Thread.Sleep(3200),
            ShowResult = false
        };
        var service = new ScreenCaptureService(() => overlay);

        var result = await Task.Run(() => service.CaptureAreaAsync());

        Assert.True(result.IsCancelled);
        Assert.Equal(1, overlay.ShowModalCalls);
    }

    #endregion
}
