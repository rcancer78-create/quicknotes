using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using QuickNotes.App.Models;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class ImportExportWindow : Window
{
    public ImportExportWindow(ImportExportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = result =>
        {
            DialogResult = result;
            Close();
        };

        EncryptedArchivePasswordBox.PasswordChanged += (_, _) =>
            viewModel.SetEncryptedArchivePasswordInput(EncryptedArchivePasswordBox.Password);
        EncryptedArchivePasswordConfirmBox.PasswordChanged += (_, _) =>
            viewModel.SetEncryptedArchivePasswordConfirmInput(EncryptedArchivePasswordConfirmBox.Password);
        EncryptedArchiveRecoveryConfirmBox.PasswordChanged += (_, _) =>
            viewModel.SetRecoveryKeyConfirmationInput(EncryptedArchiveRecoveryConfirmBox.Password);
        EncryptedArchiveRotatePasswordBox.PasswordChanged += (_, _) =>
            viewModel.SetEncryptedArchiveRotatePasswordInput(EncryptedArchiveRotatePasswordBox.Password);
        EncryptedArchiveRotateRecoveryBox.PasswordChanged += (_, _) =>
            viewModel.SetEncryptedArchiveRotateRecoveryInput(EncryptedArchiveRotateRecoveryBox.Password);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Closing -= OnClosing;
            viewModel.ClearEncryptedArchiveSecrets();
            ClearSecretBoxes();
        };
    }

    private void MappingTag_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImportExportViewModel vm)
        {
            vm.ApplyFolderMappingEdits();
        }
    }

    private void DuplicateAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ImportExportViewModel vm || sender is not System.Windows.Controls.ComboBox combo || combo.DataContext is not ImportItemPreview item)
        {
            return;
        }

        if (combo.SelectedValue is ImportDuplicateAction action)
        {
            vm.ChangeDuplicateAction(item, action);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is ImportExportViewModel vm && vm.IsEncryptedArchiveBusy)
        {
            e.Cancel = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportExportViewModel.HasPendingRecoveryVerification)
            && DataContext is ImportExportViewModel vm
            && vm.HasPendingRecoveryVerification)
        {
            EncryptedArchivePasswordBox.Password = string.Empty;
            EncryptedArchivePasswordConfirmBox.Password = string.Empty;
            EncryptedArchiveRotatePasswordBox.Password = string.Empty;
            EncryptedArchiveRotateRecoveryBox.Password = string.Empty;
        }

        if (e.PropertyName == nameof(ImportExportViewModel.ShownRecoveryKey)
            && DataContext is ImportExportViewModel cleared
            && string.IsNullOrEmpty(cleared.ShownRecoveryKey))
        {
            EncryptedArchiveRecoveryConfirmBox.Password = string.Empty;
        }
    }

    private void ClearSecretBoxes()
    {
        EncryptedArchivePasswordBox.Password = string.Empty;
        EncryptedArchivePasswordConfirmBox.Password = string.Empty;
        EncryptedArchiveRecoveryConfirmBox.Password = string.Empty;
        EncryptedArchiveRotatePasswordBox.Password = string.Empty;
        EncryptedArchiveRotateRecoveryBox.Password = string.Empty;
    }
}
