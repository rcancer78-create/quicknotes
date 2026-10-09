using System;

namespace QuickNotes.App.ViewModels;

/// <summary>
/// User-facing copy for QNAR create / recovery-key verification. Secrets are never part of this text.
/// </summary>
public static class EncryptedArchiveSetupUi
{
    public const string RecoveryKeyDoesNotReplaceNotePassword =
        "Recovery key не заменяет пароль защищённой заметки. Без пароля заметки её содержимое из архива не восстановить.";

    public const string RecoveryKeyShowOnceWarning =
        "Сохраните этот ключ отдельно от файла архива и от пароля архива. Он показывается только один раз. Приложение не копирует его в буфер обмена и не записывает в настройки, базу или журнал.";

    public const string SetupIncompleteHint =
        "Настройка не завершена, пока вы не введёте recovery key обратно и контрольное восстановление (dry-run) созданного архива не пройдёт успешно.";

    public const string OfflineCopyConfirm =
        "Записать recovery key в новый текстовый файл? Это секрет: выберите путь отдельно от .qnar. Приложение не копирует ключ в буфер обмена и не записывает его в настройки, базу или журнал. Существующий файл не будет перезаписан.";

    public const string OfflinePrintConfirm =
        "Напечатать recovery key? Это секрет. Распечатка должна храниться отдельно от файла архива. Приложение не копирует ключ в буфер обмена.";

    public const string RotateConfirm =
        "Выпустить новый recovery key для этого .qnar? Старый ключ перестанет открывать ИМЕННО ЭТОТ файл. Копии старого файла, если они остались, старым ключом всё ещё открываются — удалите их сами. Пароль архива не меняется. При сбое исходный файл останется без изменений.";

    public const string RotateSectionHint =
        "Ротация меняет только recovery-wrap существующего .qnar. Нужен текущий recovery key или пароль архива. Новый ключ показывается один раз; старый после успеха этот файл не открывает.";

    public const string OfflineCopySectionHint =
        "Офлайн-копия — печать или новый файл с контрольной суммой. Путь по умолчанию — «Документы», не каталог архива. Нужно явное подтверждение.";

    public static string LastSuccessBanner(string? path, DateTime? utc)
    {
        if (string.IsNullOrWhiteSpace(path) || !utc.HasValue)
        {
            return "Последний успешный зашифрованный архив: настройка ещё не завершена (нужно контрольное восстановление recovery key).";
        }

        return $"Последний успешный зашифрованный архив: {path}\nВремя (UTC): {utc.Value:u}";
    }
}
