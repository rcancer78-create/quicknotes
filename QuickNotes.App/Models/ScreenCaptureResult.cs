using System;

namespace QuickNotes.App.Models;

public sealed class ScreenCaptureResult
{
    public bool Success { get; }
    public byte[]? ImageBytes { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsCancelled { get; }
    public string? ErrorMessage { get; }

    public ScreenCaptureResult(bool success, byte[]? imageBytes, int width, int height, bool isCancelled, string? errorMessage)
    {
        Success = success;
        ImageBytes = imageBytes;
        Width = width;
        Height = height;
        IsCancelled = isCancelled;
        ErrorMessage = errorMessage;
    }

    public static ScreenCaptureResult Succeeded(byte[] imageBytes, int width, int height) =>
        new(true, imageBytes, width, height, false, null);

    public static ScreenCaptureResult Cancelled() =>
        new(false, null, 0, 0, true, null);

    public static ScreenCaptureResult Failed(string errorMessage) =>
        new(false, null, 0, 0, false, errorMessage);
}
