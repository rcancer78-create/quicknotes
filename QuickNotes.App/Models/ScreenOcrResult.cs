using System;

namespace QuickNotes.App.Models;

public sealed class ScreenOcrResult
{
    public bool Success { get; }
    public string? Text { get; }
    public bool IsCancelled { get; }
    public bool IsTimeout { get; }
    public bool IsEmpty { get; }
    public string? ErrorMessage { get; }
    public string? LanguageTag { get; }
    public byte[]? ImageBytes { get; }

    public ScreenOcrResult(bool success, string? text, bool isCancelled, bool isEmpty, string? errorMessage, string? languageTag, byte[]? imageBytes = null, bool isTimeout = false)
    {
        Success = success;
        Text = text;
        IsCancelled = isCancelled;
        IsTimeout = isTimeout;
        IsEmpty = isEmpty;
        ErrorMessage = errorMessage;
        LanguageTag = languageTag;
        ImageBytes = imageBytes;
    }

    public static ScreenOcrResult Succeeded(string text, string? languageTag = null, byte[]? imageBytes = null) =>
        new(true, text, false, false, null, languageTag, imageBytes);

    public static ScreenOcrResult Cancelled() =>
        new(false, null, true, false, null, null);

    public static ScreenOcrResult TimedOut(string? message = null) =>
        new(false, null, false, false, message ?? Helpers.UserFacingOperationError.Timeout, null, isTimeout: true);

    public static ScreenOcrResult Empty(string message = "Текст на выбранной области не обнаружен.") =>
        new(false, string.Empty, false, true, message, null);

    public static ScreenOcrResult Failed(string errorMessage) =>
        new(false, null, false, false, errorMessage, null);
}
