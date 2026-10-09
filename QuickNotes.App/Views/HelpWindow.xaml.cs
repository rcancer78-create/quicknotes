using System.Windows;
using QuickNotes.App.Helpers;

namespace QuickNotes.App.Views;

public partial class HelpWindow : Window
{
    public HelpWindow(string? ocrGesture = null)
    {
        InitializeComponent();
        ShortcutsHelpText.Text = ShortcutCatalog.BuildHelpHotkeysParagraph(
            string.IsNullOrWhiteSpace(ocrGesture) ? ShortcutCatalog.DefaultOcrGesture : ocrGesture);
    }
}
