namespace QuickNotes.App.Helpers;

/// <summary>
/// First-level user-task copy for settings, cloud and note transfer (UX-C13).
/// Implementation terms stay under «Дополнительно» or in warnings.
/// </summary>
public static class UserTaskCopy
{
    public const string CloudSectionTitle = "Копии заметок в облаке";
    public const string CloudSectionLead =
        "Заметки остаются на этом компьютере. По желанию можно хранить зашифрованные копии, чтобы открыть их на другом устройстве.";
    public const string CloudWizardButton = "Подключить через мастер…";
    public const string CloudWizardAutomationName = "Подключить облачные копии через мастер";
    public const string CloudEnableCheckbox = "Хранить копии в облаке";
    public const string CloudAdvancedHeader = "Дополнительно";
    public const string CloudTestButton = "Проверить доступ";
    public const string CloudSaveButton = "Сохранить";
    public const string CloudDisconnectButton = "Отключить облако";
    public const string CloudCryptoWarning =
        "Мастер-пароль защищает данные перед отправкой. Без него расшифровать заметки невозможно. Сохраните пароль в надёжном месте.";
    public const string CloudRequirements =
        "Рекомендуется закрытое хранилище и ключ только с правами на эти копии. Данные шифруются на устройстве (AES-256-GCM, ключ из мастер-пароля через PBKDF2-HMAC-SHA256) до отправки.";

    public const string LocalBackupLead = "На этом компьютере хранятся последние 7 копий базы.";
    public const string PortableArchiveHint =
        "Переносимый архив с паролем (.qnar) создаётся во вкладке «Архив с паролем» окна «Перенос заметок». Это не та же копия, что кнопка выше.";

    public const string TransferWindowTitle = "Перенос заметок";
    public const string ExportTab = "Сохранить копии";
    public const string ImportTab = "Загрузить копии";
    public const string ArchiveTab = "Архив с паролем";
    public const string ExportLead =
        "Сохранить незащищённые заметки в обычные файлы на этом компьютере. Архив с паролем — на соседней вкладке.";
    public const string MenuTransfer = "Перенос заметок…";
    public const string MenuCloudWizard = "Подключить облако…";
}
