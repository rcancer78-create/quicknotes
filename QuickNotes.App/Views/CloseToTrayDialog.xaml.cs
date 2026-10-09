using System.Windows;
using QuickNotes.App.Helpers;

namespace QuickNotes.App.Views;

public partial class CloseToTrayDialog : Window
{
    public CloseMainWindowDecision Decision { get; private set; } = CloseMainWindowDecision.Cancel;

    public CloseToTrayDialog()
    {
        InitializeComponent();
        PromptText.Text = CloseMainWindowPromptText.Message;
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        Decision = CloseMainWindowDecision.Hide;
        DialogResult = true;
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Decision = CloseMainWindowDecision.Exit;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Decision = CloseMainWindowDecision.Cancel;
        DialogResult = false;
    }
}
