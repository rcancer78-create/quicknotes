using System.Windows;
using QuickNotes.App.Helpers;

namespace QuickNotes.App.Views;

public partial class UnsavedEditorDialog : Window
{
    public UnsavedEditorDecision Decision { get; private set; } = UnsavedEditorDecision.Stay;

    public UnsavedEditorDialog()
    {
        InitializeComponent();
        PromptText.Text = UnsavedEditorPromptText.Message;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Decision = UnsavedEditorDecision.Save;
        DialogResult = true;
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        Decision = UnsavedEditorDecision.Discard;
        DialogResult = true;
    }

    private void OnStay(object sender, RoutedEventArgs e)
    {
        Decision = UnsavedEditorDecision.Stay;
        DialogResult = false;
    }
}
