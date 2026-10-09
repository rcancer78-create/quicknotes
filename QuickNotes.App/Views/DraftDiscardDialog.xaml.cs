using System.Windows;

namespace QuickNotes.App.Views;

public partial class DraftDiscardDialog : Window
{
    public bool Decision { get; private set; }

    public DraftDiscardDialog()
    {
        InitializeComponent();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Decision = false;
        DialogResult = false;
        Close();
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        Decision = true;
        DialogResult = true;
        Close();
    }
}
