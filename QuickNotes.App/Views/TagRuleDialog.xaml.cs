using System.Windows;
using System.Windows.Input;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class TagRuleDialog : Window
{
    public TagRuleDialog(TagRuleViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };

        RequiredTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                viewModel.AddRequiredTermCommand.Execute(null);
                e.Handled = true;
            }
        };

        AnyTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                viewModel.AddAnyTermCommand.Execute(null);
                e.Handled = true;
            }
        };

        ExcludedTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                viewModel.AddExcludedTermCommand.Execute(null);
                e.Handled = true;
            }
        };

        Loaded += (s, e) => RequiredTextBox.Focus();
    }
}
