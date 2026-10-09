using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using AppOcrResult = QuickNotes.App.Models.OcrResult;

namespace QuickNotes.App.Services;

public sealed class WindowsOcrService : IOcrService
{
    public bool IsSupported
    {
        get
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) &&
                       OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static Language? ResolveBestLanguage()
    {
        try
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            if (available == null || available.Count == 0)
            {
                return null;
            }

            // 1. Prefer Russian if available
            var ru = available.FirstOrDefault(l =>
                l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
            if (ru != null) return ru;

            // 2. Prefer current UI / system language
            var currentUiName = CultureInfo.CurrentUICulture.Name;
            var currentUiTwoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var matchUi = available.FirstOrDefault(l =>
                l.LanguageTag.Equals(currentUiName, StringComparison.OrdinalIgnoreCase) ||
                l.LanguageTag.StartsWith(currentUiTwoLetter, StringComparison.OrdinalIgnoreCase));
            if (matchUi != null) return matchUi;

            // 3. Prefer English
            var en = available.FirstOrDefault(l =>
                l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (en != null) return en;

            // 4. First available language
            return available[0];
        }
        catch
        {
            return null;
        }
    }

    public uint? MaxImageDimensionOverride { get; set; }

    internal uint EffectiveMaxImageDimension => MaxImageDimensionOverride ?? OcrEngine.MaxImageDimension;

    /// <summary>
    /// Calculates proportionally downscaled target dimensions that fit within maxDimension, preserving aspect ratio.
    /// Does not upscale images if both dimensions are already within limit.
    /// Guarantees strictly positive non-zero dimensions without integer overflow.
    /// </summary>
    public static (uint Width, uint Height) CalculateTargetDimensions(uint width, uint height, uint maxDimension)
    {
        if (width == 0) width = 1;
        if (height == 0) height = 1;
        if (maxDimension == 0) maxDimension = 1;

        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height);
        }

        double scale = Math.Min((double)maxDimension / width, (double)maxDimension / height);

        long scaledW = (long)Math.Round(width * scale, MidpointRounding.AwayFromZero);
        long scaledH = (long)Math.Round(height * scale, MidpointRounding.AwayFromZero);

        uint targetWidth = (uint)Math.Clamp(scaledW, 1L, (long)maxDimension);
        uint targetHeight = (uint)Math.Clamp(scaledH, 1L, (long)maxDimension);

        return (targetWidth, targetHeight);
    }

    public static (int Width, int Height) CalculateTargetDimensions(int width, int height, int maxDimension)
    {
        uint uWidth = width <= 0 ? 1u : (uint)width;
        uint uHeight = height <= 0 ? 1u : (uint)height;
        uint uMax = maxDimension <= 0 ? 1u : (uint)maxDimension;

        var (tw, th) = CalculateTargetDimensions(uWidth, uHeight, uMax);
        return ((int)tw, (int)th);
    }

    public async Task<AppOcrResult> RecognizeTextAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            return AppOcrResult.Failed("Изображение для распознавания текста отсутствует.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                return AppOcrResult.Failed("Распознавание текста поддерживается только на Windows 10 и новее.");
            }

            var availableLanguages = OcrEngine.AvailableRecognizerLanguages;
            if (availableLanguages == null || availableLanguages.Count == 0)
            {
                return AppOcrResult.Failed("Распознавание текста Windows недоступно: не установлены поддерживаемые языковые пакеты OCR.");
            }

            var selectedLanguage = ResolveBestLanguage();
            OcrEngine? engine = null;
            if (selectedLanguage != null)
            {
                engine = OcrEngine.TryCreateFromLanguage(selectedLanguage);
            }

            if (engine == null)
            {
                engine = OcrEngine.TryCreateFromUserProfileLanguages();
            }

            if (engine == null && availableLanguages.Count > 0)
            {
                engine = OcrEngine.TryCreateFromLanguage(availableLanguages[0]);
            }

            if (engine == null)
            {
                return AppOcrResult.Failed("Не удалось инициализировать движок распознавания текста Windows.");
            }

            using var memoryStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(memoryStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync().AsTask(cancellationToken);
            }

            var decoder = await BitmapDecoder.CreateAsync(memoryStream).AsTask(cancellationToken);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken);

            if (softwareBitmap.PixelWidth <= 0 || softwareBitmap.PixelHeight <= 0)
            {
                return AppOcrResult.Failed("Некорректные размеры изображения для распознавания текста.");
            }

            uint maxDimension = EffectiveMaxImageDimension;
            uint actualWidth = (uint)softwareBitmap.PixelWidth;
            uint actualHeight = (uint)softwareBitmap.PixelHeight;

            SoftwareBitmap? ocrBitmap = null;
            bool disposeOcrBitmap = false;

            try
            {
                if (actualWidth > maxDimension || actualHeight > maxDimension)
                {
                    var (targetWidth, targetHeight) = CalculateTargetDimensions(actualWidth, actualHeight, maxDimension);

                    var transform = new BitmapTransform
                    {
                        ScaledWidth = targetWidth,
                        ScaledHeight = targetHeight,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };

                    SoftwareBitmap? scaledBitmap = null;
                    try
                    {
                        scaledBitmap = await decoder.GetSoftwareBitmapAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            transform,
                            ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
                    }
                    catch
                    {
                        scaledBitmap = await ResizeSoftwareBitmapViaEncoderAsync(softwareBitmap, targetWidth, targetHeight, cancellationToken);
                    }

                    if (scaledBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                        scaledBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                    {
                        var converted = SoftwareBitmap.Convert(scaledBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                        scaledBitmap.Dispose();
                        scaledBitmap = converted;
                    }

                    ocrBitmap = scaledBitmap;
                    disposeOcrBitmap = true;
                }
                else if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                         softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                {
                    ocrBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    disposeOcrBitmap = true;
                }
                else
                {
                    ocrBitmap = softwareBitmap;
                    disposeOcrBitmap = false;
                }

                var ocrResult = await engine.RecognizeAsync(ocrBitmap).AsTask(cancellationToken);
                if (ocrResult == null || ocrResult.Lines == null || ocrResult.Lines.Count == 0)
                {
                    return AppOcrResult.Empty("Текст на выбранной области не обнаружен.");
                }

                var lines = ocrResult.Lines
                    .Select(l => l.Text?.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (lines.Count == 0)
                {
                    return AppOcrResult.Empty("Текст на выбранной области не обнаружен.");
                }

                string recognizedText = string.Join(Environment.NewLine, lines);
                return AppOcrResult.Succeeded(recognizedText, engine.RecognizerLanguage?.LanguageTag);
            }
            finally
            {
                if (disposeOcrBitmap && ocrBitmap != null)
                {
                    ocrBitmap.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            return AppOcrResult.Failed("Распознавание текста отменено пользователем.");
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("WindowsOcrService.RecognizeText", ex);
            return AppOcrResult.Failed($"Ошибка распознавания текста Windows: {ex.Message}");
        }
    }

    private static async Task<SoftwareBitmap> ResizeSoftwareBitmapViaEncoderAsync(
        SoftwareBitmap sourceBitmap,
        uint targetWidth,
        uint targetHeight,
        CancellationToken cancellationToken)
    {
        SoftwareBitmap bitmapToEncode = sourceBitmap;
        bool converted = false;
        if (sourceBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            sourceBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            bitmapToEncode = SoftwareBitmap.Convert(sourceBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            converted = true;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken);
            encoder.SetSoftwareBitmap(bitmapToEncode);
            await encoder.FlushAsync().AsTask(cancellationToken);

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken);
        }
        finally
        {
            if (converted)
            {
                bitmapToEncode.Dispose();
            }
        }
    }
}
