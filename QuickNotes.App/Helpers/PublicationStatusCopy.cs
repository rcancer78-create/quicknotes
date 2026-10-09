using QuickNotes.App.Models.Sync;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Single source of truth for the first-level local/cloud status vocabulary (UX-C11).
/// Technical details stay in tooltips; the five first-level phrases are shared by cards and the status bar.
/// </summary>
public static class PublicationStatusCopy
{
    public const string SavedOnThisPc = "Сохранено на этом ПК";
    public const string WaitingToSend = "Ожидает отправки";
    public const string InCloud = "В облаке";
    public const string Conflict = "Конфликт";
    public const string Error = "Ошибка";

    public static string FirstLevel(LocalCommitSyncStatus status) => status switch
    {
        LocalCommitSyncStatus.SavedLocally => SavedOnThisPc,
        LocalCommitSyncStatus.PendingUpload => WaitingToSend,
        LocalCommitSyncStatus.Syncing => WaitingToSend,
        LocalCommitSyncStatus.Synchronized => InCloud,
        LocalCommitSyncStatus.Conflict => Conflict,
        LocalCommitSyncStatus.Error => Error,
        _ => SavedOnThisPc
    };

    public static string FirstLevel(MainSyncStatus status, bool hasSuccessfulSync) => status switch
    {
        MainSyncStatus.Disabled => SavedOnThisPc,
        MainSyncStatus.NeedsConfig => SavedOnThisPc,
        MainSyncStatus.Ready => hasSuccessfulSync ? InCloud : SavedOnThisPc,
        MainSyncStatus.Syncing => WaitingToSend,
        MainSyncStatus.Offline => WaitingToSend,
        MainSyncStatus.Conflicts => Conflict,
        MainSyncStatus.Error => Error,
        _ => SavedOnThisPc
    };

    public static string Icon(LocalCommitSyncStatus status) => status switch
    {
        LocalCommitSyncStatus.SavedLocally => "💾",
        LocalCommitSyncStatus.PendingUpload => "⏳",
        LocalCommitSyncStatus.Syncing => "🔄",
        LocalCommitSyncStatus.Synchronized => "☁️",
        LocalCommitSyncStatus.Conflict => "⚠️",
        LocalCommitSyncStatus.Error => "❌",
        _ => "💾"
    };

    public static string Detail(LocalCommitSyncStatus status) => status switch
    {
        LocalCommitSyncStatus.SavedLocally => "Заметка надёжно сохранена в локальной базе. Облако не используется или не настроено.",
        LocalCommitSyncStatus.PendingUpload => "Заметка сохранена на этом ПК и ждёт отправки в облако.",
        LocalCommitSyncStatus.Syncing => "Заметка сохранена на этом ПК. Сейчас идёт передача в облако.",
        LocalCommitSyncStatus.Synchronized => "Заметка сохранена на этом ПК и есть в облаке.",
        LocalCommitSyncStatus.Conflict => "Нужно выбрать, какую версию оставить: на этом ПК или в облаке.",
        LocalCommitSyncStatus.Error => "Заметка сохранена на этом ПК. Отправка в облако не удалась и будет повторена.",
        _ => "Заметка сохранена на этом ПК."
    };

    public static string Tooltip(LocalCommitSyncStatus status) => FirstLevel(status) + "\n" + Detail(status);
}
