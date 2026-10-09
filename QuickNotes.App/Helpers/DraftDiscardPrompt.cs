using System.Windows;

namespace QuickNotes.App.Helpers;

public static class DraftDiscardPrompt
{
    public const string Title = "Удалить черновик?";
    public const string Message = "Вы уверены, что хотите безвозвратно удалить этот черновик? Восстановить его будет невозможно.";

    public static bool Show(Window? owner)
    {
        var dialog = new QuickNotes.App.Views.DraftDiscardDialog();
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Decision;
    }
}
