using System;

namespace QuickNotes.App.Models;

public sealed class OcrResult
{
    public bool Success { get; }
    public string Text { get; }
    public string? ErrorMessage { get; }
    public string? LanguageTag { get; }

    public OcrResult(bool success, string text, string? errorMessage, string? languageTag = null)
    {
        Success = success;
        Text = text;
        ErrorMessage = errorMessage;
        LanguageTag = languageTag;
    }

    public static OcrResult Succeeded(string text, string? languageTag = null) =>
        new(true, text, null, languageTag);

    public static OcrResult Empty(string? message = null) =>
        new(false, string.Empty, message ?? "Текст на выбранной области не обнаружен.");

    public static OcrResult Failed(string errorMessage) =>
        new(false, string.Empty, errorMessage);
}
