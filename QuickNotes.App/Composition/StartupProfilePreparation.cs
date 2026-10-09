using System;
using System.IO;
using QuickNotes.App.Data;
using QuickNotes.App.Services;

namespace QuickNotes.App.Composition;

public enum StartupProfilePreparationKind
{
    Ready = 0,
    InvalidIsolatedPath = 1,
    DirectoryUnavailable = 2
}

public sealed record StartupProfilePreparationResult(
    StartupProfilePreparationKind Kind,
    string ProfileDirectory,
    string UserMessage)
{
    public bool Succeeded => Kind == StartupProfilePreparationKind.Ready;
}

/// <summary>
/// Creates the resolved profile directory before any integrity, recovery, or SQLite access.
/// Isolated paths stay fail-closed; creation failures are not treated as a corrupt database.
/// </summary>
public static class StartupProfilePreparation
{
    public static StartupProfilePreparationResult Prepare(IsolatedProfileAppOptions isolated)
    {
        ArgumentNullException.ThrowIfNull(isolated);

        string profile;
        try
        {
            profile = ApplicationCompositionRoot.ResolveProfileDirectory(isolated);
        }
        catch (Exception ex)
        {
            return Fail(
                StartupProfilePreparationKind.DirectoryUnavailable,
                string.Empty,
                "Не удалось определить каталог данных QuickNotes. Запуск отменён.",
                ex);
        }

        bool isIsolated = isolated.IsIsolated && !string.IsNullOrWhiteSpace(isolated.ProfileDirectory);
        if (isIsolated)
        {
            try
            {
                QuickNotesDbContext.ValidateIsolatedProfilePath(profile);
            }
            catch (Exception ex)
            {
                return Fail(
                    StartupProfilePreparationKind.InvalidIsolatedPath,
                    profile,
                    "Изолированный профиль указывает на недопустимый путь (рабочий каталог QuickNotes, его предок или потомок). Запуск отменён.",
                    ex);
            }
        }

        return EnsureDirectoryExists(profile);
    }

    public static StartupProfilePreparationResult EnsureDirectoryExists(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return Fail(
                StartupProfilePreparationKind.DirectoryUnavailable,
                string.Empty,
                "Не задан каталог данных QuickNotes. Запуск отменён.",
                ex: null);
        }

        string profile;
        try
        {
            profile = Path.GetFullPath(profileDirectory);
        }
        catch (Exception ex)
        {
            return Fail(
                StartupProfilePreparationKind.DirectoryUnavailable,
                profileDirectory,
                "Путь каталога данных QuickNotes некорректен. Запуск отменён.",
                ex);
        }

        try
        {
            if (File.Exists(profile))
            {
                return Fail(
                    StartupProfilePreparationKind.DirectoryUnavailable,
                    profile,
                    "Не удалось подготовить каталог данных QuickNotes: по указанному пути находится файл, а не папка. Запуск отменён.",
                    ex: null);
            }

            Directory.CreateDirectory(profile);

            if (!Directory.Exists(profile))
            {
                return Fail(
                    StartupProfilePreparationKind.DirectoryUnavailable,
                    profile,
                    "Не удалось создать каталог данных QuickNotes. Проверьте права доступа. Запуск отменён.",
                    ex: null);
            }

            return new StartupProfilePreparationResult(
                StartupProfilePreparationKind.Ready,
                profile,
                string.Empty);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return Fail(
                StartupProfilePreparationKind.DirectoryUnavailable,
                profile,
                "Не удалось создать или открыть каталог данных QuickNotes. Проверьте права доступа и путь. Запуск отменён.",
                ex);
        }
        catch (Exception ex)
        {
            return Fail(
                StartupProfilePreparationKind.DirectoryUnavailable,
                profile,
                "Не удалось подготовить каталог данных QuickNotes. Запуск отменён.",
                ex);
        }
    }

    private static StartupProfilePreparationResult Fail(
        StartupProfilePreparationKind kind,
        string profileDirectory,
        string actionable,
        Exception? ex)
    {
        string detail = ex == null ? string.Empty : ErrorLogService.Sanitize(ex.Message);
        string message = string.IsNullOrEmpty(detail)
            ? actionable
            : actionable + " " + detail;
        return new StartupProfilePreparationResult(kind, profileDirectory, message);
    }
}
