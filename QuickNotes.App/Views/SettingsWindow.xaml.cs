using System.Windows;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SecretAccessKeyBox.PasswordChanged += (s, e) => viewModel.SetSecretAccessKeyInput(SecretAccessKeyBox.Password);
        EncryptionPasswordBox.PasswordChanged += (s, e) => viewModel.SetEncryptionPasswordInput(EncryptionPasswordBox.Password);
        EncryptionPasswordConfirmBox.PasswordChanged += (s, e) => viewModel.SetEncryptionPasswordConfirmInput(EncryptionPasswordConfirmBox.Password);
        RotationOldPasswordBox.PasswordChanged += (s, e) => viewModel.SetRotationOldPasswordInput(RotationOldPasswordBox.Password);
        RotationNewPasswordBox.PasswordChanged += (s, e) => viewModel.SetRotationNewPasswordInput(RotationNewPasswordBox.Password);
        RotationNewPasswordConfirmBox.PasswordChanged += (s, e) => viewModel.SetRotationNewPasswordConfirmInput(RotationNewPasswordConfirmBox.Password);

        viewModel.ClearPasswordInputsRequested += () =>
        {
            SecretAccessKeyBox.Password = string.Empty;
            EncryptionPasswordBox.Password = string.Empty;
            EncryptionPasswordConfirmBox.Password = string.Empty;
            RotationOldPasswordBox.Password = string.Empty;
            RotationNewPasswordBox.Password = string.Empty;
            RotationNewPasswordConfirmBox.Password = string.Empty;
        };

        viewModel.RequestOpenCloudSetupWizard += () =>
        {
            var wizard = new CloudSetupWizardWindow(viewModel)
            {
                Owner = IsVisible ? this : null,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            wizard.ShowDialog();
        };

        viewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };

        Closed += (s, e) =>
        {
            viewModel.NotifyClosed();
            SecretAccessKeyBox.Password = string.Empty;
            EncryptionPasswordBox.Password = string.Empty;
            EncryptionPasswordConfirmBox.Password = string.Empty;
            RotationOldPasswordBox.Password = string.Empty;
            RotationNewPasswordBox.Password = string.Empty;
            RotationNewPasswordConfirmBox.Password = string.Empty;
        };
    }
}
