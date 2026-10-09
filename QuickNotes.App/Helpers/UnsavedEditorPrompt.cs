using System.Windows;
using QuickNotes.App.Views;

namespace QuickNotes.App.Helpers;

public static class UnsavedEditorPrompt
{
    public static UnsavedEditorDecision Show(Window? owner)
    {
        var dialog = new UnsavedEditorDialog();
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Decision;
    }
}
