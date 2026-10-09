using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace QuickNotes.App.Views;

/// <summary>
/// Compact password dialog used for note protection.
/// Supports three modes:
///  - SetPassword: two PasswordBoxes (password + confirmation)
///  - Unlock / Verify: single PasswordBox
///  - ChangePassword: single PasswordBox (current password)
/// The password is read directly from the PasswordBox and never bound to a TextBox property.
/// </summary>
public partial class ProtectionPasswordDialog : Window
{
    public enum DialogMode
    {
        SetPassword,
        Unlock,
        VerifyCurrent
    }

    private readonly DialogMode _mode;

    public string Password => PasswordInput.Password;

    public ProtectionPasswordDialog(
        string title,
        string message,
        DialogMode mode,
        string? warning = null,
        Window? owner = null)
    {
        InitializeComponent();
        _mode = mode;
        Title = title;
        DataContext = this;

        if (mode == DialogMode.SetPassword)
        {
            ConfirmLabel.Visibility = Visibility.Visible;
            ConfirmInput.Visibility = Visibility.Visible;
            WarningText.Text = warning ?? "Восстановить забытый пароль невозможно. Убедитесь, что вы запомните пароль.";
        }
        else
        {
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmInput.Visibility = Visibility.Collapsed;
            WarningText.Text = warning ?? string.Empty;
        }

        MessageText.Text = message;

        if (owner != null && owner.IsVisible)
        {
            Owner = owner;
        }

        Loaded += (_, _) =>
        {
            PasswordInput.Focus();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordInput.Password))
        {
            MessageBox.Show(this, "Пароль не может быть пустым.", "Пароль", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_mode == DialogMode.SetPassword)
        {
            if (!string.Equals(PasswordInput.Password, ConfirmInput.Password, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "Пароли не совпадают. Повторите ввод.", "Пароль", MessageBoxButton.OK, MessageBoxImage.Warning);
                ConfirmInput.Clear();
                ConfirmInput.Focus();
                return;
            }
        }

        DialogResult = true;
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OkButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
