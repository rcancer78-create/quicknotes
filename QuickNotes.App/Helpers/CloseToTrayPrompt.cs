using System.Windows;
using QuickNotes.App.Views;

namespace QuickNotes.App.Helpers;

public static class CloseToTrayPrompt
{
    public static CloseMainWindowDecision Show(Window? owner)
    {
        var dialog = new CloseToTrayDialog();
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Decision;
    }
}
