namespace QuickNotes.App.Helpers;

public enum UnsavedEditorDecision
{
    Stay,
    Save,
    Discard
}

public static class UnsavedEditorPromptText
{
    public const string Title = "Несохранённые изменения";

    public const string Message =
        "Сохранить изменения в заметку?\n\n" +
        "«Сохранить» запишет текст в заметку на этом компьютере и удалит журнал черновика.\n" +
        "«Не сохранять» отменит правки в редакторе и удалит журнал черновика. Эти правки не будут предложены при следующем запуске.\n" +
        "«Вернуться» оставит редактор открытым. Журнал черновика сохранится.";

    public const string EmptyTextWarning = "Текст заметки не может быть пустым. Добавьте текст или выберите «Не сохранять».";

    public const string SaveFailure =
        "Не удалось сохранить изменения. Редактор и журнал черновика оставлены открытыми; повторите попытку или вернитесь к редактированию.";
}
