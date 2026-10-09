namespace QuickNotes.App.Helpers;

public enum CloseMainWindowDecision
{
    Cancel,
    Hide,
    Exit
}

public static class CloseMainWindowPromptText
{
    public const string Title = "QuickNotes продолжит работать";

    public const string Message =
        "Закрытие окна не завершает программу: захват текста и мгновенное сохранение остаются доступны из системного трея.\n\n" +
        "«Скрыть» оставит QuickNotes в трее. «Выйти» полностью завершит программу.\n" +
        "Выбор запоминается; его можно изменить в настройках.";
}
