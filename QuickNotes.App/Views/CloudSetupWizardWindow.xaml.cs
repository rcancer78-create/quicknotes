using System;
using System.Threading.Tasks;
using System.Windows;
using QuickNotes.App.Helpers;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Views;

public partial class CloudSetupWizardWindow : Window
{
    private readonly SettingsViewModel _settings;
    private int _step;

    public CloudSetupWizardWindow(SettingsViewModel settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        DataContext = settings;
        InitializeComponent();

        WizardSecretBox.PasswordChanged += (_, _) => _settings.SetSecretAccessKeyInput(WizardSecretBox.Password);
        WizardPasswordBox.PasswordChanged += (_, _) => _settings.SetEncryptionPasswordInput(WizardPasswordBox.Password);
        WizardPasswordConfirmBox.PasswordChanged += (_, _) => _settings.SetEncryptionPasswordConfirmInput(WizardPasswordConfirmBox.Password);

        Closed += (_, _) =>
        {
            WizardSecretBox.Password = string.Empty;
            WizardPasswordBox.Password = string.Empty;
            WizardPasswordConfirmBox.Password = string.Empty;
        };

        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        Step0Panel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5Panel.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == 5 ? "Сохранить и включить" : "Далее";

        StepTitle.Text = step switch
        {
            0 => "Шаг 1 из 6. Зачем это нужно",
            1 => "Шаг 2 из 6. Подготовка в консоли Yandex Cloud",
            2 => "Шаг 3 из 6. Имя бакета",
            3 => "Шаг 4 из 6. Ключи доступа",
            4 => "Шаг 5 из 6. Мастер-пароль",
            _ => "Шаг 6 из 6. Проверка и сохранение"
        };

        StepHint.Text = step switch
        {
            2 => "Имя бакета — как в консоли, без https://.",
            3 => "Если ключ уже сохранён на этом компьютере, можно оставить поля как есть и перейти дальше.",
            4 => "На втором устройстве введите тот же пароль, иначе обмен не расшифруется.",
            5 => "После ошибки можно вернуться назад, исправить данные и проверить снова.",
            _ => string.Empty
        };

        Step2Error.Text = string.Empty;
        Step3Error.Text = string.Empty;
        Step4Error.Text = string.Empty;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        NextButton.IsEnabled = false;
        AsyncEventBridge.Fire(NextClickAsync, "CloudSetupWizard.Next", RestoreNextButton);
    }

    private async Task NextClickAsync()
    {
        try
        {
            if (!ValidateCurrentStep())
            {
                return;
            }

            if (_step < 5)
            {
                ShowStep(_step + 1);
                return;
            }

            _settings.SyncEnabled = true;
            bool saved = await _settings.SaveSyncSettingsAsync().ConfigureAwait(true);
            if (!saved)
            {
                StepHint.Text = "Не удалось сохранить параметры. Проверьте ключи, бакет и пароль, затем повторите.";
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("CloudSetupWizard.Next", ex);
            StepHint.Text = UserFacingOperationError.GenericFailure;
        }
        finally
        {
            RestoreNextButton();
        }
    }

    private void RestoreNextButton()
    {
        if (IsLoaded)
        {
            NextButton.IsEnabled = true;
        }
    }

    private bool ValidateCurrentStep()
    {
        if (_step == 2)
        {
            try
            {
                SyncCloudSettingsValidator.ValidateBucket(_settings.SyncBucket);
                if (!string.IsNullOrWhiteSpace(_settings.SyncPrefix))
                {
                    SyncCloudSettingsValidator.ValidatePrefix(_settings.SyncPrefix);
                }
            }
            catch (SyncValidationException ex)
            {
                Step2Error.Text = ex.Message;
                return false;
            }
        }

        if (_step == 3)
        {
            bool hasId = !string.IsNullOrWhiteSpace(_settings.SyncAccessKeyId) && !_settings.SyncAccessKeyId.StartsWith("••••", StringComparison.Ordinal);
            bool hasSecret = !string.IsNullOrWhiteSpace(WizardSecretBox.Password);
            if ((hasId || hasSecret) && (!hasId || !hasSecret))
            {
                Step3Error.Text = "Укажите и идентификатор ключа, и секретный ключ — либо оба поля, если ключи уже сохранены.";
                return false;
            }
        }

        if (_step == 4)
        {
            string password = WizardPasswordBox.Password ?? string.Empty;
            string confirm = WizardPasswordConfirmBox.Password ?? string.Empty;
            if (!string.IsNullOrEmpty(password) && !string.Equals(password, confirm, StringComparison.Ordinal))
            {
                Step4Error.Text = "Пароль и подтверждение не совпадают.";
                return false;
            }
        }

        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
