using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace QuickNotes.App.ViewModels;

public class ThemeOption
{
    public AppTheme Theme { get; }
    public string Title { get; }

    public ThemeOption(AppTheme theme, string title)
    {
        Theme = theme;
        Title = title;
    }
}

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly BackupService _backupService;
    private readonly BoundedOwnership _backupOwnership = new();
    private CancellationTokenSource? _backupCts;
    private bool _isClosed;
    private readonly IS3CredentialsStorage _credentialsStorage;
    private readonly ISyncPasswordStorage _passwordStorage;
    private readonly ISyncCloudCoordinator? _coordinator;
    private readonly ISyncScheduler? _syncScheduler;
    private readonly ICloudUsageService? _cloudUsageService;
    private readonly ICloudCleanupService? _cloudCleanupService;
    private readonly ISyncPasswordRotationService? _passwordRotationService;
    private readonly ICloudRetentionService? _cloudRetentionService;
    private readonly ITaskReminderScheduler? _taskReminderScheduler;
    private readonly ICloudTransportFactory? _transportFactory;

    internal ISyncScheduler? SyncSchedulerForTests => _syncScheduler;
    internal ICloudUsageService? CloudUsageServiceForTests => _cloudUsageService;
    internal ITaskReminderScheduler? TaskReminderSchedulerForTests => _taskReminderScheduler;

    // --- Sync Cloud Fields ---
    private bool _syncEnabled;
    private string _syncBucket = string.Empty;
    private string? _syncPrefix;
    private string _syncEndpoint = SyncCloudSettings.DefaultYandexEndpoint;
    private string _syncRegion = SyncCloudSettings.DefaultYandexRegion;
    private string _syncAccessKeyId = string.Empty;
    private bool _hasSavedCredentials;
    private bool _hasSavedPassword;
    private string _syncCredentialsStatusText = string.Empty;
    private string _syncPasswordStatusText = string.Empty;
    private string _syncConnectionStatusText = string.Empty;
    private bool _isTestingConnection;
    private bool _isBackupBusy;
    private bool _isConnectionSuccess;
    private bool _isConnectionError;
    private bool _isConnectionWarning;

    // Auto-sync triggers & queue state fields
    private bool _autoSyncOnChanges = true;
    private bool _autoSyncOnStartup = true;
    private bool _autoSyncPeriodic = false;
    private int _periodicIntervalMinutes = 30;
    private bool _syncAttachments = true;
    private int _maxAttachmentSyncMb = 100;
    private string _syncQueueStatusText = "Нет изменений";
    private string _syncLastSuccessText = "Ещё не выполнялась";
    private string _syncLastAttemptText = "Ещё не выполнялась";
    private string _syncMetricsText = "Нет данных";
    private string _syncConflictsCountText = "0";
    private string _cloudUsageText = "Не вычислялся";
    private string _cloudQuotaStatusText = "Квота: объём ещё не вычислен.";
    private string _cloudCleanupPreviewText = "Предварительный просмотр очистки ещё не выполнялся.";
    private string _cloudRetentionStatusText = "Очистка старых пакетов и поколений ещё не выполнялась.";
    private bool _isCloudRetentionBusy;
    private CloudRetentionPreview? _packageRetentionPreview;
    private CloudRetentionPreview? _generationRetentionPreview;
    private bool _isCloudCleanupBusy;
    private CloudCleanupPreview? _cleanupPreview;
    private bool _isCloudUsageBusy;
    private string _passwordRotationStatusText = "Ротация мастер-пароля ещё не выполнялась.";
    private bool _isPasswordRotationBusy;
    private PasswordRotationPreview? _rotationPreview;
    private string _rotationOldPasswordInput = string.Empty;
    private string _rotationNewPasswordInput = string.Empty;
    private string _rotationNewPasswordConfirmInput = string.Empty;

    private string _secretAccessKeyInput = string.Empty;
    private string _encryptionPasswordInput = string.Empty;
    private string _encryptionPasswordConfirmInput = string.Empty;

    private CancellationTokenSource? _testConnectionCts;

    private ThemeOption _selectedThemeItem;

    private bool _hotkeyCtrl;
    private bool _hotkeyShift;
    private bool _hotkeyAlt;
    private bool _hotkeyWin;
    private string _selectedKey = "Space";

    private bool _instantHotkeyCtrl;
    private bool _instantHotkeyShift;
    private bool _instantHotkeyAlt;
    private bool _instantHotkeyWin;
    private string _selectedInstantKey = "Space";

    private bool _ocrHotkeyCtrl;
    private bool _ocrHotkeyShift;
    private bool _ocrHotkeyAlt;
    private bool _ocrHotkeyWin;
    private string _selectedOcrKey = "O";

    private bool _runOnStartup;
    private bool _startMinimizedToTray;
    private bool _preferCloseToTray;
    private bool _showNotificationOnSave;
    private bool _localRemindersEnabled = true;
    private string _localRemindersStatusText = string.Empty;
    private bool _autoPurgeTrashEnabled;
    private int _maxAttachmentSizeMb = 25;
    private string _backupStatusText = string.Empty;

    private int _settingsSectionIndex;

    public bool HotkeyCtrl
    {
        get => _hotkeyCtrl;
        set => SetProperty(ref _hotkeyCtrl, value);
    }

    public bool HotkeyShift
    {
        get => _hotkeyShift;
        set => SetProperty(ref _hotkeyShift, value);
    }

    public bool HotkeyAlt
    {
        get => _hotkeyAlt;
        set => SetProperty(ref _hotkeyAlt, value);
    }

    public bool HotkeyWin
    {
        get => _hotkeyWin;
        set => SetProperty(ref _hotkeyWin, value);
    }

    public string SelectedKey
    {
        get => _selectedKey;
        set => SetProperty(ref _selectedKey, value);
    }

    public bool InstantHotkeyCtrl
    {
        get => _instantHotkeyCtrl;
        set => SetProperty(ref _instantHotkeyCtrl, value);
    }

    public bool InstantHotkeyShift
    {
        get => _instantHotkeyShift;
        set => SetProperty(ref _instantHotkeyShift, value);
    }

    public bool InstantHotkeyAlt
    {
        get => _instantHotkeyAlt;
        set => SetProperty(ref _instantHotkeyAlt, value);
    }

    public bool InstantHotkeyWin
    {
        get => _instantHotkeyWin;
        set => SetProperty(ref _instantHotkeyWin, value);
    }

    public string SelectedInstantKey
    {
        get => _selectedInstantKey;
        set => SetProperty(ref _selectedInstantKey, value);
    }

    public bool OcrHotkeyCtrl
    {
        get => _ocrHotkeyCtrl;
        set => SetProperty(ref _ocrHotkeyCtrl, value);
    }

    public bool OcrHotkeyShift
    {
        get => _ocrHotkeyShift;
        set => SetProperty(ref _ocrHotkeyShift, value);
    }

    public bool OcrHotkeyAlt
    {
        get => _ocrHotkeyAlt;
        set => SetProperty(ref _ocrHotkeyAlt, value);
    }

    public bool OcrHotkeyWin
    {
        get => _ocrHotkeyWin;
        set => SetProperty(ref _ocrHotkeyWin, value);
    }

    public string SelectedOcrKey
    {
        get => _selectedOcrKey;
        set => SetProperty(ref _selectedOcrKey, value);
    }

    public List<string> AvailableKeys { get; }

    public bool RunOnStartup
    {
        get => _runOnStartup;
        set => SetProperty(ref _runOnStartup, value);
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set => SetProperty(ref _startMinimizedToTray, value);
    }

    public bool PreferCloseToTray
    {
        get => _preferCloseToTray;
        set => SetProperty(ref _preferCloseToTray, value);
    }

    public bool ShowNotificationOnSave
    {
        get => _showNotificationOnSave;
        set => SetProperty(ref _showNotificationOnSave, value);
    }

    public bool LocalRemindersEnabled
    {
        get => _localRemindersEnabled;
        set
        {
            if (SetProperty(ref _localRemindersEnabled, value))
            {
                RefreshLocalRemindersStatus();
            }
        }
    }

    public string LocalRemindersStatusText
    {
        get => _localRemindersStatusText;
        private set => SetProperty(ref _localRemindersStatusText, value);
    }

    public bool AutoPurgeTrashEnabled
    {
        get => _autoPurgeTrashEnabled;
        set => SetProperty(ref _autoPurgeTrashEnabled, value);
    }

    public int MaxAttachmentSizeMb
    {
        get => _maxAttachmentSizeMb;
        set => SetProperty(ref _maxAttachmentSizeMb, value);
    }

    public List<ThemeOption> AvailableThemes { get; }

    public ThemeOption SelectedThemeItem
    {
        get => _selectedThemeItem;
        set => SetProperty(ref _selectedThemeItem, value);
    }

    public string BackupDirectoryText => _backupService.BackupDirectory;

    public string BackupStatusText
    {
        get => _backupStatusText;
        set => SetProperty(ref _backupStatusText, value);
    }

    internal TimeSpan BackupTimeout { get; set; } = BoundedOperation.BackupTimeout;

    public bool IsBackupBusy
    {
        get => _isBackupBusy;
        private set
        {
            if (SetProperty(ref _isBackupBusy, value))
            {
                (CreateBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string EncryptedArchiveLastSuccessBanner =>
        EncryptedArchiveSetupUi.LastSuccessBanner(
            _settingsService.CurrentSettings.LastEncryptedArchivePath,
            _settingsService.CurrentSettings.LastEncryptedArchiveUtc);

    public bool SyncEnabled
    {
        get => _syncEnabled;
        set
        {
            if (SetProperty(ref _syncEnabled, value))
            {
                RaiseSyncCommandsCanExecute();
            }
        }
    }

    public string SyncBucket
    {
        get => _syncBucket;
        set
        {
            if (SetProperty(ref _syncBucket, value))
            {
                RaiseSyncCommandsCanExecute();
            }
        }
    }

    public string? SyncPrefix
    {
        get => _syncPrefix;
        set => SetProperty(ref _syncPrefix, value);
    }

    public string SyncEndpoint
    {
        get => _syncEndpoint;
        set
        {
            if (SetProperty(ref _syncEndpoint, value))
            {
                RaiseSyncCommandsCanExecute();
            }
        }
    }

    public string SyncRegion
    {
        get => _syncRegion;
        set => SetProperty(ref _syncRegion, value);
    }

    public string SyncAccessKeyId
    {
        get => _syncAccessKeyId;
        set
        {
            if (SetProperty(ref _syncAccessKeyId, value))
            {
                RaiseSyncCommandsCanExecute();
            }
        }
    }

    public bool HasSavedCredentials
    {
        get => _hasSavedCredentials;
        private set => SetProperty(ref _hasSavedCredentials, value);
    }

    public bool HasSavedPassword
    {
        get => _hasSavedPassword;
        private set => SetProperty(ref _hasSavedPassword, value);
    }

    public string SyncCredentialsStatusText
    {
        get => _syncCredentialsStatusText;
        private set => SetProperty(ref _syncCredentialsStatusText, value);
    }

    public string SyncPasswordStatusText
    {
        get => _syncPasswordStatusText;
        private set => SetProperty(ref _syncPasswordStatusText, value);
    }

    public string SyncConnectionStatusText
    {
        get => _syncConnectionStatusText;
        set
        {
            if (SetProperty(ref _syncConnectionStatusText, value))
            {
                OnPropertyChanged(nameof(HasConnectionStatusText));
            }
        }
    }

    public bool HasConnectionStatusText => !string.IsNullOrEmpty(SyncConnectionStatusText);

    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (SetProperty(ref _isTestingConnection, value))
            {
                RaiseSyncCommandsCanExecute();
            }
        }
    }

    public bool IsConnectionSuccess
    {
        get => _isConnectionSuccess;
        private set => SetProperty(ref _isConnectionSuccess, value);
    }

    public bool IsConnectionError
    {
        get => _isConnectionError;
        private set => SetProperty(ref _isConnectionError, value);
    }

    public bool IsConnectionWarning
    {
        get => _isConnectionWarning;
        private set => SetProperty(ref _isConnectionWarning, value);
    }

    public void SetSecretAccessKeyInput(string? secret)
    {
        _secretAccessKeyInput = secret ?? string.Empty;
        RaiseSyncCommandsCanExecute();
    }

    public void SetEncryptionPasswordInput(string? password)
    {
        _encryptionPasswordInput = password ?? string.Empty;
    }

    public void SetEncryptionPasswordConfirmInput(string? confirm)
    {
        _encryptionPasswordConfirmInput = confirm ?? string.Empty;
    }

    public void SetRotationOldPasswordInput(string? password) => _rotationOldPasswordInput = password ?? string.Empty;
    public void SetRotationNewPasswordInput(string? password) => _rotationNewPasswordInput = password ?? string.Empty;
    public void SetRotationNewPasswordConfirmInput(string? password) => _rotationNewPasswordConfirmInput = password ?? string.Empty;

    public event Action? ClearPasswordInputsRequested;

    public bool AutoSyncOnChanges
    {
        get => _autoSyncOnChanges;
        set => SetProperty(ref _autoSyncOnChanges, value);
    }

    public bool AutoSyncOnStartup
    {
        get => _autoSyncOnStartup;
        set => SetProperty(ref _autoSyncOnStartup, value);
    }

    public bool AutoSyncPeriodic
    {
        get => _autoSyncPeriodic;
        set => SetProperty(ref _autoSyncPeriodic, value);
    }

    public int PeriodicIntervalMinutes
    {
        get => _periodicIntervalMinutes;
        set => SetProperty(ref _periodicIntervalMinutes, value);
    }

    public bool SyncAttachments
    {
        get => _syncAttachments;
        set => SetProperty(ref _syncAttachments, value);
    }

    public const int DefaultMaxAttachmentSyncMb = 100;
    public const int MaxAllowedAttachmentSyncMb = 1024;
    public const string InvalidMaxAttachmentSyncSizeErrorMessage = "Лимит размера облачного вложения должен быть положительным числом от 1 до 1024 МБ.";

    public int MaxAttachmentSyncMb
    {
        get => _maxAttachmentSyncMb;
        set => SetProperty(ref _maxAttachmentSyncMb, value);
    }

    public bool IsMaxAttachmentSyncSizeValid => MaxAttachmentSyncMb > 0 && MaxAttachmentSyncMb <= MaxAllowedAttachmentSyncMb;

    public bool ValidateMaxAttachmentSyncSize(out string? errorMessage)
    {
        if (!IsMaxAttachmentSyncSizeValid)
        {
            errorMessage = InvalidMaxAttachmentSyncSizeErrorMessage;
            return false;
        }

        errorMessage = null;
        return true;
    }

    public string SyncQueueStatusText
    {
        get => _syncQueueStatusText;
        private set => SetProperty(ref _syncQueueStatusText, value);
    }

    public string SyncLastSuccessText
    {
        get => _syncLastSuccessText;
        private set => SetProperty(ref _syncLastSuccessText, value);
    }

    public string SyncLastAttemptText
    {
        get => _syncLastAttemptText;
        private set => SetProperty(ref _syncLastAttemptText, value);
    }

    public string SyncMetricsText
    {
        get => _syncMetricsText;
        private set => SetProperty(ref _syncMetricsText, value);
    }

    public string SyncConflictsCountText
    {
        get => _syncConflictsCountText;
        private set => SetProperty(ref _syncConflictsCountText, value);
    }

    public string CloudUsageText
    {
        get => _cloudUsageText;
        private set => SetProperty(ref _cloudUsageText, value);
    }

    public bool IsCloudUsageBusy
    {
        get => _isCloudUsageBusy;
        private set
        {
            if (SetProperty(ref _isCloudUsageBusy, value))
            {
                (RefreshCloudUsageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string CloudUsageExplanationText => "Занятый объём в пространстве QuickNotes (префикс). Не является общим размером всего бакета. Пороги свободного тарифа: предупреждение с 800 МиБ, запрет новых облачных вложений с 950 МиБ при лимите 1 ГиБ.";

    public string CloudQuotaStatusText
    {
        get => _cloudQuotaStatusText;
        private set => SetProperty(ref _cloudQuotaStatusText, value);
    }

    public string CloudCleanupPreviewText
    {
        get => _cloudCleanupPreviewText;
        private set => SetProperty(ref _cloudCleanupPreviewText, value);
    }

    public bool IsCloudCleanupBusy
    {
        get => _isCloudCleanupBusy;
        private set
        {
            if (SetProperty(ref _isCloudCleanupBusy, value))
            {
                (PreviewCloudCleanupCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExecuteCloudCleanupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string PasswordRotationStatusText
    {
        get => _passwordRotationStatusText;
        private set => SetProperty(ref _passwordRotationStatusText, value);
    }

    public bool IsPasswordRotationBusy
    {
        get => _isPasswordRotationBusy;
        private set
        {
            if (SetProperty(ref _isPasswordRotationBusy, value))
            {
                (PreviewPasswordRotationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExecutePasswordRotationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand SaveSyncSettingsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand DeleteSyncCredentialsCommand { get; }
    public ICommand RefreshCloudUsageCommand { get; }
    public string CloudRetentionStatusText
    {
        get => _cloudRetentionStatusText;
        private set => SetProperty(ref _cloudRetentionStatusText, value);
    }

    public bool IsCloudRetentionBusy
    {
        get => _isCloudRetentionBusy;
        private set
        {
            if (SetProperty(ref _isCloudRetentionBusy, value))
            {
                (PreviewPackageRetentionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExecutePackageRetentionCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PreviewOldGenerationCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ExecuteOldGenerationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand PreviewCloudCleanupCommand { get; }
    public ICommand ExecuteCloudCleanupCommand { get; }
    public ICommand PreviewPasswordRotationCommand { get; }
    public ICommand ExecutePasswordRotationCommand { get; }
    public ICommand PreviewPackageRetentionCommand { get; }
    public ICommand ExecutePackageRetentionCommand { get; }
    public ICommand PreviewOldGenerationCommand { get; }
    public ICommand ExecuteOldGenerationCommand { get; }

    public int SettingsSectionIndex
    {
        get => _settingsSectionIndex;
        set => SetProperty(ref _settingsSectionIndex, value);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CreateBackupCommand { get; }
    public ICommand OpenTemplateManagementCommand { get; }
    public ICommand OpenCloudSetupWizardCommand { get; }

    public Action? OpenTemplateManagementAction { get; set; }

    public event Action<bool?>? RequestClose;
    public event Action? RequestOpenCloudSetupWizard;

    public const string InvalidMaxAttachmentSizeErrorMessage = "Максимальный размер вложения должен быть положительным числом (в МБ).";

    public bool IsMaxAttachmentSizeValid => MaxAttachmentSizeMb > 0;

    public bool ValidateMaxAttachmentSize(out string? errorMessage)
    {
        if (!IsMaxAttachmentSizeValid)
        {
            errorMessage = InvalidMaxAttachmentSizeErrorMessage;
            return false;
        }

        errorMessage = null;
        return true;
    }

    private Action<string, string, MessageBoxImage> _showMessage;
    private Func<string, string, bool> _confirmAction;

    public Action<string, string, MessageBoxImage> ShowMessageAction
    {
        get => _showMessage;
        set => _showMessage = value ?? ((msg, title, icon) => MessageBox.Show(msg, title, MessageBoxButton.OK, icon));
    }

    public Func<string, string, bool> ConfirmAction
    {
        get => _confirmAction;
        set => _confirmAction = value ?? DefaultConfirm;
    }

    private static bool DefaultConfirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public SettingsViewModel(
        SettingsService settingsService,
        GlobalHotkeyService hotkeyService,
        BackupService backupService,
        IS3CredentialsStorage credentialsStorage,
        ISyncPasswordStorage passwordStorage,
        Action<string, string, MessageBoxImage>? showMessage = null,
        ISyncCloudCoordinator? coordinator = null,
        ISyncScheduler? syncScheduler = null,
        ICloudUsageService? cloudUsageService = null,
        ICloudCleanupService? cloudCleanupService = null,
        ISyncPasswordRotationService? passwordRotationService = null,
        ICloudRetentionService? cloudRetentionService = null,
        ITaskReminderScheduler? taskReminderScheduler = null,
        ICloudTransportFactory? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(credentialsStorage);
        ArgumentNullException.ThrowIfNull(passwordStorage);

        _settingsService = settingsService;
        _hotkeyService = hotkeyService;
        _backupService = backupService;
        _backupOwnership.DrainCompleted += () => RunOnDispatcher(() =>
        {
            if (_isClosed)
            {
                return;
            }

            IsBackupBusy = false;
        });
        _showMessage = showMessage ?? ((msg, title, icon) => MessageBox.Show(msg, title, MessageBoxButton.OK, icon));
        _credentialsStorage = credentialsStorage;
        _passwordStorage = passwordStorage;
        _coordinator = coordinator;
        _syncScheduler = syncScheduler;
        _cloudUsageService = cloudUsageService;
        _cloudCleanupService = cloudCleanupService;
        _passwordRotationService = passwordRotationService;
        _cloudRetentionService = cloudRetentionService;
        _taskReminderScheduler = taskReminderScheduler;
        _transportFactory = transportFactory;
        _confirmAction = DefaultConfirm;

        var keys = new List<string> { "Space", "Enter", "Tab", "Insert", "Delete", "Home", "End", "PageUp", "PageDown" };
        for (char c = 'A'; c <= 'Z'; c++)
        {
            keys.Add(c.ToString());
        }
        for (int i = 0; i <= 9; i++)
        {
            keys.Add($"D{i}");
        }
        for (int f = 1; f <= 12; f++)
        {
            keys.Add($"F{f}");
        }
        AvailableKeys = keys;

        var s = _settingsService.CurrentSettings;
        HotkeyCtrl = s.HotkeyCtrl;
        HotkeyShift = s.HotkeyShift;
        HotkeyAlt = s.HotkeyAlt;
        HotkeyWin = s.HotkeyWin;
        SelectedKey = AvailableKeys.Contains(s.HotkeyKey) ? s.HotkeyKey : "Space";

        InstantHotkeyCtrl = s.InstantHotkeyCtrl;
        InstantHotkeyShift = s.InstantHotkeyShift;
        InstantHotkeyAlt = s.InstantHotkeyAlt;
        InstantHotkeyWin = s.InstantHotkeyWin;
        SelectedInstantKey = AvailableKeys.Contains(s.InstantHotkeyKey) ? s.InstantHotkeyKey : "Space";

        OcrHotkeyCtrl = s.OcrHotkeyCtrl;
        OcrHotkeyShift = s.OcrHotkeyShift;
        OcrHotkeyAlt = s.OcrHotkeyAlt;
        OcrHotkeyWin = s.OcrHotkeyWin;
        SelectedOcrKey = AvailableKeys.Contains(s.OcrHotkeyKey) ? s.OcrHotkeyKey : ShortcutCatalog.DefaultOcrKey;

        RunOnStartup = s.RunOnStartup;
        StartMinimizedToTray = s.StartMinimizedToTray;
        PreferCloseToTray = s.PreferCloseToTray;
        ShowNotificationOnSave = s.ShowNotificationOnSave;
        LocalRemindersEnabled = s.LocalRemindersEnabled;
        RefreshLocalRemindersStatus();
        AutoPurgeTrashEnabled = s.AutoPurgeTrashEnabled;
        MaxAttachmentSizeMb = s.MaxAttachmentSizeMb > 0 ? s.MaxAttachmentSizeMb : 25;

        AvailableThemes = new List<ThemeOption>
        {
            new(AppTheme.System, "Системная (по теме Windows)"),
            new(AppTheme.Light, "Светлая"),
            new(AppTheme.Dark, "Тёмная"),
            new(AppTheme.Green, "Зелёная")
        };
        _selectedThemeItem = AvailableThemes.FirstOrDefault(t => t.Theme == s.Theme) ?? AvailableThemes[0];

        // Sync settings initialization
        var cloud = s.CloudSync ?? new SyncCloudSettings();
        _syncEnabled = cloud.Enabled;
        _syncBucket = cloud.Bucket ?? string.Empty;
        _syncPrefix = cloud.Prefix;
        _syncEndpoint = string.IsNullOrWhiteSpace(cloud.Endpoint) ? SyncCloudSettings.DefaultYandexEndpoint : cloud.Endpoint;
        _syncRegion = string.IsNullOrWhiteSpace(cloud.Region) ? SyncCloudSettings.DefaultYandexRegion : cloud.Region;

        _autoSyncOnChanges = cloud.AutoSyncOnChanges;
        _autoSyncOnStartup = cloud.AutoSyncOnStartup;
        _autoSyncPeriodic = cloud.AutoSyncPeriodic;
        _periodicIntervalMinutes = Math.Max(SyncCloudSettings.MinPeriodicIntervalMinutes, cloud.PeriodicIntervalMinutes);
        _syncAttachments = cloud.SyncAttachments;
        long maxSyncBytes = cloud.MaxAttachmentSyncBytes > 0 ? cloud.MaxAttachmentSyncBytes : SyncCloudSettings.DefaultMaxAttachmentSyncBytes;
        _maxAttachmentSyncMb = Math.Clamp((int)Math.Round(maxSyncBytes / (1024d * 1024d)), 1, MaxAllowedAttachmentSyncMb);

        _hasSavedCredentials = _credentialsStorage.HasCredentials();
        _hasSavedPassword = _passwordStorage.HasPassword();
        _syncAccessKeyId = _hasSavedCredentials ? "•••••••• (сохранён)" : string.Empty;

        UpdateSyncStatusTexts();

        SaveCommand = new RelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        CreateBackupCommand = new RelayCommand(CreateBackupAsync, () => !IsBackupBusy);
        OpenTemplateManagementCommand = new RelayCommand(() => OpenTemplateManagementAction?.Invoke());
        OpenCloudSetupWizardCommand = new RelayCommand(() => RequestOpenCloudSetupWizard?.Invoke());

        SaveSyncSettingsCommand = new RelayCommand(async () => await SaveSyncSettingsAsync());
        TestConnectionCommand = new RelayCommand(async () => await TestConnectionAsync(), CanTestConnection);
        DeleteSyncCredentialsCommand = new RelayCommand(async () => await DeleteSyncCredentialsAsync());
        RefreshCloudUsageCommand = new RelayCommand(async () => await RefreshCloudUsageAsync(), () => !IsCloudUsageBusy);
        PreviewCloudCleanupCommand = new RelayCommand(async () => await PreviewCloudCleanupAsync(), () => !IsCloudCleanupBusy);
        ExecuteCloudCleanupCommand = new RelayCommand(async () => await ExecuteCloudCleanupAsync(), () => !IsCloudCleanupBusy && _cleanupPreview != null && _cleanupPreview.IsComplete);
        PreviewPasswordRotationCommand = new RelayCommand(async () => await PreviewPasswordRotationAsync(), () => !IsPasswordRotationBusy);
        ExecutePasswordRotationCommand = new RelayCommand(async () => await ExecutePasswordRotationAsync(), () => !IsPasswordRotationBusy && _rotationPreview != null && _rotationPreview.IsComplete);
        PreviewPackageRetentionCommand = new RelayCommand(async () => await PreviewPackageRetentionAsync(), () => !IsCloudRetentionBusy);
        ExecutePackageRetentionCommand = new RelayCommand(async () => await ExecutePackageRetentionAsync(), () => !IsCloudRetentionBusy && _packageRetentionPreview?.IsComplete == true);
        PreviewOldGenerationCommand = new RelayCommand(async () => await PreviewOldGenerationAsync(), () => !IsCloudRetentionBusy);
        ExecuteOldGenerationCommand = new RelayCommand(async () => await ExecuteOldGenerationAsync(), () => !IsCloudRetentionBusy && _generationRetentionPreview?.IsComplete == true);

        if (_syncScheduler != null)
        {
            _syncScheduler.StatusChanged += OnSchedulerStatusChanged;
        }

        RequestClose += _ => NotifyClosed();

        UpdateBackupStatus();
    }

    private void OnSchedulerStatusChanged(object? sender, SyncSchedulerStatusChangedEventArgs e)
    {
        RunOnDispatcher(UpdateSyncStatusTexts);
    }

    private void UpdateSyncStatusTexts()
    {
        SyncCredentialsStatusText = HasSavedCredentials
            ? "Учетные данные S3: сохранены в DPAPI"
            : "Учетные данные S3: не настроены";

        SyncPasswordStatusText = HasSavedPassword
            ? "Пароль шифрования: сохранён в DPAPI"
            : "Пароль шифрования: не задан";

        if (_syncScheduler != null)
        {
            SyncQueueStatusText = _syncScheduler.QueueStatusDescription;
            SyncLastSuccessText = _syncScheduler.LastSuccessTimeUtc.HasValue
                ? _syncScheduler.LastSuccessTimeUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                : "Ещё не выполнялась";
            SyncLastAttemptText = _syncScheduler.LastAttemptTimeUtc.HasValue
                ? _syncScheduler.LastAttemptTimeUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
                : "Ещё не выполнялась";
            SyncConflictsCountText = _syncScheduler.UnresolvedConflictsCount.ToString();

            if (_syncScheduler.LastResult != null)
            {
                var r = _syncScheduler.LastResult;
                int applied = r.RemoteEntitiesApplied.Created + r.RemoteEntitiesApplied.Updated + r.RemoteEntitiesApplied.Deleted;
                int uploaded = r.PackageUploaded ? 1 : 0;
                SyncMetricsText = $"Получено: {r.RemotePackagesPulled}, применено: {applied}, отправлено: {uploaded}, пропущено: {r.RemoteEntitiesSkipped}";
            }
            else
            {
                SyncMetricsText = "Нет данных";
            }
        }

        if (_cloudUsageService?.CachedUsage != null)
        {
            CloudUsageText = _cloudUsageService.CachedUsage.FormatUsageText();
            CloudQuotaStatusText = _cloudUsageService.CachedUsage.FormatQuotaStatusText();
        }
    }

    public async Task RefreshCloudUsageAsync(CancellationToken ct = default)
    {
        if (IsCloudUsageBusy || _cloudUsageService == null) return;

        IsCloudUsageBusy = true;
        CloudUsageText = "Вычисление объёма…";
        try
        {
            var res = await _cloudUsageService.CalculateUsageAsync(forceRefresh: true, ct).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                CloudUsageText = res.FormatUsageText();
                CloudQuotaStatusText = res.FormatQuotaStatusText();
            });
        }
        catch (Exception ex)
        {
            RunOnDispatcher(() =>
            {
                CloudUsageText = $"Ошибка: {ex.Message}";
                CloudQuotaStatusText = CloudQuotaPolicy.FormatStatus(null);
            });
        }
        finally
        {
            RunOnDispatcher(() =>
            {
                IsCloudUsageBusy = false;
            });
        }
    }

    public async Task PreviewCloudCleanupAsync(CancellationToken ct = default)
    {
        if (IsCloudCleanupBusy || _cloudCleanupService == null)
        {
            if (_cloudCleanupService == null)
                _showMessage("Сервис очистки облака недоступен.", "Очистка", MessageBoxImage.Warning);
            return;
        }

        string? password = await _passwordStorage.LoadPasswordAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            _showMessage("Для предварительного просмотра очистки нужен сохранённый пароль шифрования.", "Очистка", MessageBoxImage.Warning);
            return;
        }

        IsCloudCleanupBusy = true;
        CloudCleanupPreviewText = "Построение предварительного просмотра…";
        try
        {
            var preview = await _cloudCleanupService.PreviewAsync(password, ct).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                _cleanupPreview = preview;
                CloudCleanupPreviewText = FormatCleanupPreview(preview);
                (ExecuteCloudCleanupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.PreviewCleanup", ex.GetType().Name);
            RunOnDispatcher(() =>
            {
                _cleanupPreview = null;
                CloudCleanupPreviewText = "Не удалось построить предварительный просмотр. Удаление не выполнялось.";
            });
        }
        finally
        {
            RunOnDispatcher(() => IsCloudCleanupBusy = false);
        }
    }

    public async Task ExecuteCloudCleanupAsync(CancellationToken ct = default)
    {
        if (IsCloudCleanupBusy || _cloudCleanupService == null || _cleanupPreview == null)
            return;

        if (!_cleanupPreview.IsComplete || _cleanupPreview.Candidates.Count == 0)
        {
            _showMessage(_cleanupPreview.BlockReason ?? "Нет безопасных кандидатов для удаления.", "Очистка", MessageBoxImage.Information);
            return;
        }

        string confirmText =
            $"Будет удалено {_cleanupPreview.Candidates.Count} недостижимых blob-объектов " +
            $"(примерно {CloudUsageResult.FormatBytes(_cleanupPreview.ReclaimableBytes)}). " +
            "Пакеты, указатели и локальные файлы не удаляются. " +
            "Blob из инкрементальной истории пакетов не включаются. Продолжить?";

        if (!_confirmAction(confirmText, "Подтверждение очистки облака"))
            return;

        string? password = await _passwordStorage.LoadPasswordAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
        {
            _showMessage("Пароль шифрования недоступен. Удаление не выполнялось.", "Очистка", MessageBoxImage.Warning);
            return;
        }

        IsCloudCleanupBusy = true;
        try
        {
            var result = await _cloudCleanupService.ExecuteAsync(_cleanupPreview.Token, password, ct).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                CloudCleanupPreviewText = result.Summary;
                if (result.Deleted > 0)
                    _cleanupPreview = null;
                (ExecuteCloudCleanupCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
            _showMessage(result.Summary, "Очистка облака", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.ExecuteCleanup", ex.GetType().Name);
            _showMessage("Очистка прервана. Частично обработанные объекты не удаляются повторно автоматически.", "Очистка", MessageBoxImage.Warning);
        }
        finally
        {
            RunOnDispatcher(() => IsCloudCleanupBusy = false);
        }
    }

    public async Task PreviewPackageRetentionAsync(CancellationToken ct = default)
    {
        if (_cloudRetentionService == null)
        {
            _showMessage("Сервис очистки пакетов недоступен.", "Очистка пакетов", MessageBoxImage.Warning);
            return;
        }

        IsCloudRetentionBusy = true;
        try
        {
            var preview = await _cloudRetentionService.PreviewPackageRetentionAsync(ct).ConfigureAwait(false);
            _packageRetentionPreview = preview;
            CloudRetentionStatusText = preview.Summary;
            (ExecutePackageRetentionCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.PreviewPackageRetention", ex);
            CloudRetentionStatusText = "Не удалось подготовить просмотр пакетов.";
        }
        finally
        {
            IsCloudRetentionBusy = false;
        }
    }

    public async Task ExecutePackageRetentionAsync(CancellationToken ct = default)
    {
        if (_cloudRetentionService == null || _packageRetentionPreview == null)
            return;
        if (!_packageRetentionPreview.IsComplete)
        {
            _showMessage(_packageRetentionPreview.BlockReason ?? "Просмотр неполный.", "Очистка пакетов", MessageBoxImage.Information);
            return;
        }

        if (_packageRetentionPreview.Candidates.Count == 0)
        {
            _showMessage("Удалять нечего.", "Очистка пакетов", MessageBoxImage.Information);
            return;
        }

        if (!_confirmAction(
                $"Удалить {_packageRetentionPreview.Candidates.Count} устаревших пакетов (~{CloudUsageResult.FormatBytes(_packageRetentionPreview.ReclaimableBytes)})? Актуальный снимок каждого устройства сохранится.",
                "Подтверждение очистки пакетов"))
            return;

        IsCloudRetentionBusy = true;
        try
        {
            var result = await _cloudRetentionService.ExecutePackageRetentionAsync(_packageRetentionPreview.Token, ct).ConfigureAwait(false);
            CloudRetentionStatusText = result.Summary;
            if (result.Deleted > 0)
                _packageRetentionPreview = null;
            _showMessage(result.Summary, "Очистка пакетов", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.ExecutePackageRetention", ex);
            _showMessage("Очистка пакетов прервана.", "Очистка пакетов", MessageBoxImage.Warning);
        }
        finally
        {
            IsCloudRetentionBusy = false;
        }
    }

    public async Task PreviewOldGenerationAsync(CancellationToken ct = default)
    {
        if (_cloudRetentionService == null)
        {
            _showMessage("Сервис очистки поколений недоступен.", "Старое поколение", MessageBoxImage.Warning);
            return;
        }

        IsCloudRetentionBusy = true;
        try
        {
            var preview = await _cloudRetentionService.PreviewOldGenerationAsync(ct).ConfigureAwait(false);
            _generationRetentionPreview = preview;
            CloudRetentionStatusText = preview.Summary;
            (ExecuteOldGenerationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.PreviewOldGeneration", ex);
            CloudRetentionStatusText = "Не удалось подготовить просмотр старого поколения.";
        }
        finally
        {
            IsCloudRetentionBusy = false;
        }
    }

    public async Task ExecuteOldGenerationAsync(CancellationToken ct = default)
    {
        if (_cloudRetentionService == null || _generationRetentionPreview == null)
            return;
        if (!_generationRetentionPreview.IsComplete)
        {
            _showMessage(_generationRetentionPreview.BlockReason ?? "Просмотр неполный.", "Старое поколение", MessageBoxImage.Information);
            return;
        }

        if (_generationRetentionPreview.Candidates.Count == 0)
        {
            _showMessage("Старое поколение пусто.", "Старое поколение", MessageBoxImage.Information);
            return;
        }

        if (!_confirmAction(
                $"Удалить {_generationRetentionPreview.Candidates.Count} объектов предыдущего поколения (~{CloudUsageResult.FormatBytes(_generationRetentionPreview.ReclaimableBytes)})? Их нельзя расшифровать текущим паролем. Действие необратимо. Версионирование бакета не включается.",
                "Подтверждение удаления старого поколения"))
            return;

        IsCloudRetentionBusy = true;
        try
        {
            var result = await _cloudRetentionService.ExecuteOldGenerationCleanupAsync(_generationRetentionPreview.Token, ct).ConfigureAwait(false);
            CloudRetentionStatusText = result.Summary;
            if (result.Deleted > 0)
                _generationRetentionPreview = null;
            _showMessage(result.Summary, "Старое поколение", MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.ExecuteOldGeneration", ex);
            _showMessage("Удаление старого поколения прервано.", "Старое поколение", MessageBoxImage.Warning);
        }
        finally
        {
            IsCloudRetentionBusy = false;
        }
    }

    public async Task PreviewPasswordRotationAsync(CancellationToken ct = default)
    {
        if (IsPasswordRotationBusy || _passwordRotationService == null)
        {
            if (_passwordRotationService == null)
                _showMessage("Сервис ротации пароля недоступен.", "Ротация пароля", MessageBoxImage.Warning);
            return;
        }

        _syncScheduler?.CancelCurrentCycle();

        if (string.IsNullOrEmpty(_rotationOldPasswordInput))
        {
            _showMessage("Введите текущий мастер-пароль для предварительного просмотра ротации.", "Ротация пароля", MessageBoxImage.Warning);
            return;
        }

        IsPasswordRotationBusy = true;
        PasswordRotationStatusText = "Проверка текущего поколения…";
        try
        {
            var preview = await _passwordRotationService.PreviewAsync(_rotationOldPasswordInput, ct).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                _rotationPreview = preview;
                PasswordRotationStatusText = preview.IsComplete
                    ? preview.Summary
                    : (preview.BlockReason ?? "Предварительный просмотр неполный.");
                (ExecutePasswordRotationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.PreviewRotation", ex.GetType().Name);
            RunOnDispatcher(() =>
            {
                _rotationPreview = null;
                PasswordRotationStatusText = "Не удалось построить предварительный просмотр ротации. Облако не изменялось.";
            });
        }
        finally
        {
            RunOnDispatcher(() => IsPasswordRotationBusy = false);
        }
    }

    public async Task ExecutePasswordRotationAsync(CancellationToken ct = default)
    {
        if (IsPasswordRotationBusy || _passwordRotationService == null || _rotationPreview == null)
            return;

        if (!_rotationPreview.IsComplete)
        {
            _showMessage(_rotationPreview.BlockReason ?? "Предварительный просмотр неполный.", "Ротация пароля", MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrEmpty(_rotationNewPasswordInput) ||
            !string.Equals(_rotationNewPasswordInput, _rotationNewPasswordConfirmInput, StringComparison.Ordinal))
        {
            _showMessage("Новый пароль пуст или не совпадает с подтверждением.", "Ротация пароля", MessageBoxImage.Warning);
            return;
        }

        if (!_confirmAction(
            "Текущие пакеты и вложения будут перешифрованы в новое поколение. Старое поколение не удаляется автоматически. Продолжить?",
            "Подтверждение ротации пароля"))
            return;

        _syncScheduler?.CancelCurrentCycle();

        IsPasswordRotationBusy = true;
        try
        {
            var result = await _passwordRotationService.ExecuteAsync(
                _rotationPreview.Token,
                _rotationOldPasswordInput,
                _rotationNewPasswordInput,
                progress: null,
                ct).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                PasswordRotationStatusText = result.Summary;
                if (result.CloudSwitched)
                    _rotationPreview = null;
                HasSavedPassword = _passwordStorage.HasPassword();
                UpdateSyncStatusTexts();
                (ExecutePasswordRotationCommand as RelayCommand)?.RaiseCanExecuteChanged();
            });
            _showMessage(result.Summary, "Ротация пароля", result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Settings.ExecuteRotation", ex.GetType().Name);
            _showMessage("Ротация прервана. Активное поколение и локальный пароль не переключались, если облачный указатель не был обновлён.", "Ротация пароля", MessageBoxImage.Warning);
        }
        finally
        {
            RunOnDispatcher(() => IsPasswordRotationBusy = false);
        }
    }

    private static string FormatCleanupPreview(CloudCleanupPreview preview)
    {
        if (!preview.IsComplete)
            return preview.BlockReason ?? "Предварительный просмотр неполный — удаление недоступно.";

        return $"Кандидатов: {preview.Candidates.Count}, можно освободить {CloudUsageResult.FormatBytes(preview.ReclaimableBytes)}. " +
               $"Просмотрено blob: {preview.BlobObjectsExamined}, достижимых: {preview.ReachableBlobCount}. " +
               "Удаление начнётся только после явного подтверждения. " +
               "Консервативно: blob, упомянутый в любом полностью проверенном историческом пакете, не удаляется (инкрементальная история). " +
               "Условный Delete по ETag недоступен; перед удалением повторно сверяются ETag и размер, остаётся узкое окно гонки.";
    }

    private bool CanTestConnection()
    {
        if (IsTestingConnection) return false;
        if (string.IsNullOrWhiteSpace(SyncBucket)) return false;

        bool hasEnteredKeys = !string.IsNullOrWhiteSpace(SyncAccessKeyId) &&
                              !SyncAccessKeyId.StartsWith("••••") &&
                              !string.IsNullOrWhiteSpace(_secretAccessKeyInput);

        return HasSavedCredentials || hasEnteredKeys;
    }

    private void RaiseSyncCommandsCanExecute()
    {
        (TestConnectionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RunOnDispatcher(Action action)
    {
        if (AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            action();
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(action);
        }
        else
        {
            action();
        }
    }

    private void UpdateBackupStatus()
    {
        var existing = _backupService.GetExistingBackups();
        if (existing.Count > 0)
        {
            var latest = existing[0];
            BackupStatusText = $"Копий: {existing.Count} из 7. Последняя: {latest.Name}";
        }
        else
        {
            BackupStatusText = "Резервных копий пока нет. Хранятся последние 7 копий.";
        }
    }

    internal void NotifyClosed()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _backupOwnership.SuppressCallbacks();
        try
        {
            _backupCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _testConnectionCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_syncScheduler != null)
        {
            _syncScheduler.StatusChanged -= OnSchedulerStatusChanged;
        }
    }

    public async Task CreateBackupAsync()
    {
        if (IsBackupBusy || _isClosed)
        {
            return;
        }

        IsBackupBusy = true;
        _backupCts = new CancellationTokenSource();
        CancellationToken token = _backupCts.Token;
        try
        {
            var bounded = await BoundedOperation.RunAsync(
                ct => Task.Run(() => _backupService.CreateBackup(cancellationToken: ct), ct),
                BackupTimeout,
                token,
                "SettingsViewModel.CreateBackup",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                _backupOwnership).ConfigureAwait(true);

            if (_isClosed)
            {
                return;
            }

            if (bounded.Outcome == BoundedOperationOutcome.Success)
            {
                string backupPath = bounded.Value!;
                string fileName = Path.GetFileName(backupPath);
                UpdateBackupStatus();
                _showMessage(
                    $"Резервная копия успешно создана!\n\nФайл: {fileName}\nКаталог: {_backupService.BackupDirectory}",
                    "Резервная копия создана",
                    MessageBoxImage.Information);
                return;
            }

            BackupStatusText = bounded.UserMessage;
            _showMessage(bounded.UserMessage, "Резервное копирование", MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SettingsViewModel.CreateBackup", ex);
            if (_isClosed)
            {
                return;
            }

            BackupStatusText = UserFacingOperationError.GenericFailure;
            _showMessage(UserFacingOperationError.GenericFailure, "Ошибка", MessageBoxImage.Error);
        }
        finally
        {
            if (!_isClosed && !_backupOwnership.IsCancelling)
            {
                IsBackupBusy = false;
            }
        }
    }

    public async Task<bool> SaveSyncSettingsAsync()
    {
        if (SyncEnabled)
        {
            try
            {
                SyncCloudSettingsValidator.ValidateBucket(SyncBucket);
                SyncCloudSettingsValidator.ValidateEndpoint(SyncEndpoint);
                if (!string.IsNullOrEmpty(SyncPrefix))
                {
                    SyncCloudSettingsValidator.ValidatePrefix(SyncPrefix);
                }
            }
            catch (SyncValidationException ex)
            {
                _showMessage(ex.Message, "Ошибка валидации синхронизации", MessageBoxImage.Warning);
                return false;
            }

            bool hasNewKey = !string.IsNullOrWhiteSpace(SyncAccessKeyId) && !SyncAccessKeyId.StartsWith("••••");
            bool hasNewSecret = !string.IsNullOrWhiteSpace(_secretAccessKeyInput);

            if ((hasNewKey || hasNewSecret) && (!hasNewKey || !hasNewSecret))
            {
                _showMessage("Для сохранения ключей S3 необходимо указать и Access Key ID, и Secret Access Key.", "Неполные ключи S3", MessageBoxImage.Warning);
                return false;
            }

            if (!hasNewKey && !_credentialsStorage.HasCredentials())
            {
                _showMessage("Для включения синхронизации необходимо указать ключи доступа S3 (Access Key ID и Secret Access Key).", "Отсутствуют ключи S3", MessageBoxImage.Warning);
                return false;
            }

            if (!string.IsNullOrEmpty(_encryptionPasswordInput))
            {
                if (!string.Equals(_encryptionPasswordInput, _encryptionPasswordConfirmInput, StringComparison.Ordinal))
                {
                    _showMessage("Пароль шифрования и его подтверждение не совпадают.", "Несовпадение паролей", MessageBoxImage.Warning);
                    return false;
                }
            }
            else if (!_passwordStorage.HasPassword())
            {
                _showMessage("Для включения синхронизации необходимо задать пароль шифрования.", "Пароль не задан", MessageBoxImage.Warning);
                return false;
            }

            if (AutoSyncPeriodic && PeriodicIntervalMinutes < SyncCloudSettings.MinPeriodicIntervalMinutes)
            {
                _showMessage($"Интервал фонового обмена должен составлять не менее {SyncCloudSettings.MinPeriodicIntervalMinutes} минут.", "Недопустимый интервал", MessageBoxImage.Warning);
                return false;
            }

            if (!ValidateMaxAttachmentSyncSize(out var attachmentSyncSizeError))
            {
                _showMessage(attachmentSyncSizeError ?? InvalidMaxAttachmentSyncSizeErrorMessage, "Неверный размер облачного вложения", MessageBoxImage.Warning);
                return false;
            }
        }

        try
        {
            bool hasNewKey = !string.IsNullOrWhiteSpace(SyncAccessKeyId) && !SyncAccessKeyId.StartsWith("••••");
            bool hasNewSecret = !string.IsNullOrWhiteSpace(_secretAccessKeyInput);
            if (hasNewKey && hasNewSecret)
            {
                await _credentialsStorage.SaveCredentialsAsync(new S3Credentials(SyncAccessKeyId.Trim(), _secretAccessKeyInput.Trim()));
            }

            if (!string.IsNullOrEmpty(_encryptionPasswordInput) && string.Equals(_encryptionPasswordInput, _encryptionPasswordConfirmInput, StringComparison.Ordinal))
            {
                await _passwordStorage.SavePasswordAsync(_encryptionPasswordInput);
            }

            _secretAccessKeyInput = string.Empty;
            _encryptionPasswordInput = string.Empty;
            _encryptionPasswordConfirmInput = string.Empty;
            ClearPasswordInputsRequested?.Invoke();

            var current = _settingsService.CurrentSettings;
            current.CloudSync.Enabled = SyncEnabled;
            current.CloudSync.Bucket = SyncBucket.Trim();
            current.CloudSync.Prefix = string.IsNullOrWhiteSpace(SyncPrefix) ? null : SyncCloudSettingsValidator.NormalizePrefix(SyncPrefix);
            current.CloudSync.Endpoint = string.IsNullOrWhiteSpace(SyncEndpoint) ? SyncCloudSettings.DefaultYandexEndpoint : SyncEndpoint.Trim();
            current.CloudSync.Region = string.IsNullOrWhiteSpace(SyncRegion) ? SyncCloudSettings.DefaultYandexRegion : SyncRegion.Trim();
            current.CloudSync.AutoSyncOnChanges = AutoSyncOnChanges;
            current.CloudSync.AutoSyncOnStartup = AutoSyncOnStartup;
            current.CloudSync.AutoSyncPeriodic = AutoSyncPeriodic;
            current.CloudSync.PeriodicIntervalMinutes = Math.Max(SyncCloudSettings.MinPeriodicIntervalMinutes, PeriodicIntervalMinutes);
            current.CloudSync.SyncAttachments = SyncAttachments;
            current.CloudSync.MaxAttachmentSyncBytes = (long)MaxAttachmentSyncMb * 1024L * 1024L;
            _settingsService.SaveSettings(current);
            _syncScheduler?.NotifySettingsChanged();

            HasSavedCredentials = _credentialsStorage.HasCredentials();
            HasSavedPassword = _passwordStorage.HasPassword();
            SyncAccessKeyId = HasSavedCredentials ? "•••••••• (сохранён)" : string.Empty;
            UpdateSyncStatusTexts();

            SyncConnectionStatusText = "Параметры синхронизации успешно сохранены.";
            IsConnectionSuccess = true;
            IsConnectionError = false;
            IsConnectionWarning = false;
            return true;
        }
        catch (Exception ex)
        {
            _showMessage($"Не удалось сохранить параметры синхронизации: {ex.Message}", "Ошибка", MessageBoxImage.Error);
            return false;
        }
    }

    public async Task DeleteSyncCredentialsAsync()
    {
        try
        {
            await _credentialsStorage.DeleteCredentialsAsync();
            await _passwordStorage.DeletePasswordAsync();
            HasSavedCredentials = false;
            HasSavedPassword = false;
            SyncEnabled = false;
            SyncAccessKeyId = string.Empty;
            _secretAccessKeyInput = string.Empty;
            _encryptionPasswordInput = string.Empty;
            _encryptionPasswordConfirmInput = string.Empty;
            ClearPasswordInputsRequested?.Invoke();

            var current = _settingsService.CurrentSettings;
            current.CloudSync.Enabled = false;
            _settingsService.SaveSettings(current);
            _syncScheduler?.NotifySettingsChanged();

            SyncConnectionStatusText = "Ключи и пароль удалены. Синхронизация выключена.";
            IsConnectionWarning = true;
            IsConnectionSuccess = false;
            IsConnectionError = false;
            UpdateSyncStatusTexts();
            RaiseSyncCommandsCanExecute();
        }
        catch (Exception ex)
        {
            SyncConnectionStatusText = $"Ошибка при удалении ключей: {ex.Message}";
            IsConnectionError = true;
        }
    }

    public async Task TestConnectionAsync()
    {
        if (IsTestingConnection) return;

        try
        {
            SyncCloudSettingsValidator.ValidateBucket(SyncBucket);
            SyncCloudSettingsValidator.ValidateEndpoint(SyncEndpoint);
        }
        catch (SyncValidationException ex)
        {
            SyncConnectionStatusText = ex.Message;
            IsConnectionError = true;
            IsConnectionSuccess = false;
            IsConnectionWarning = false;
            return;
        }

        S3Credentials? creds = null;
        bool hasNewKey = !string.IsNullOrWhiteSpace(SyncAccessKeyId) && !SyncAccessKeyId.StartsWith("••••");
        bool hasNewSecret = !string.IsNullOrWhiteSpace(_secretAccessKeyInput);

        if (hasNewKey && hasNewSecret)
        {
            creds = new S3Credentials(SyncAccessKeyId.Trim(), _secretAccessKeyInput.Trim());
        }
        else if (_credentialsStorage.HasCredentials())
        {
            creds = await _credentialsStorage.LoadCredentialsAsync();
        }

        if (creds == null)
        {
            SyncConnectionStatusText = "Укажите или сохраните ключи S3 для проверки подключения.";
            IsConnectionError = true;
            IsConnectionSuccess = false;
            IsConnectionWarning = false;
            return;
        }

        IsTestingConnection = true;
        IsConnectionSuccess = false;
        IsConnectionError = false;
        IsConnectionWarning = false;
        SyncConnectionStatusText = "Проверка подключения…";

        _testConnectionCts = new CancellationTokenSource();
        _testConnectionCts.CancelAfter(BoundedOperation.CloudTestTimeout);
        var ct = _testConnectionCts.Token;

        await Task.Run(async () =>
        {
            try
            {
                var testSettings = new SyncCloudSettings
                {
                    Enabled = true,
                    Bucket = SyncBucket.Trim(),
                    Prefix = SyncPrefix?.Trim(),
                    Endpoint = SyncEndpoint.Trim(),
                    Region = SyncRegion.Trim(),
                    RequestTimeoutSeconds = 15,
                    MaxRetryAttempts = 1
                };

                if (_coordinator != null)
                {
                    var testRes = await _coordinator.TestConnectionAsync(ct);
                    RunOnDispatcher(() =>
                    {
                        if (testRes.Success)
                        {
                            SyncConnectionStatusText = "✓ Подключение успешно! Доступ к бакету подтверждён.";
                            IsConnectionSuccess = true;
                        }
                        else if (testRes.IsOffline)
                        {
                            SyncConnectionStatusText = $"⚠ Нет сети: не удалось подключиться к серверу ({testRes.ErrorMessage}).";
                            IsConnectionWarning = true;
                        }
                        else if (testRes.IsAuthError)
                        {
                            SyncConnectionStatusText = $"✕ Неверные ключи или права: доступ к бакету запрещён ({testRes.ErrorMessage}).";
                            IsConnectionError = true;
                        }
                        else if (testRes.ErrorMessage?.Contains("NotFound", StringComparison.OrdinalIgnoreCase) == true ||
                                 testRes.ErrorMessage?.Contains("NoSuchBucket", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            SyncConnectionStatusText = $"✕ Бакет не найден: проверьте имя бакета ({testRes.ErrorMessage}).";
                            IsConnectionError = true;
                        }
                        else
                        {
                            SyncConnectionStatusText = $"✕ Ошибка подключения: {testRes.ErrorMessage}";
                            IsConnectionError = true;
                        }
                    });
                }
                else if (_transportFactory != null)
                {
                    var credProvider = new FixedCredentialsProvider(creds);
                    using var transport = _transportFactory.Create(testSettings, credProvider);
                    bool ok = await transport.TestConnectionAsync(ct);
                    RunOnDispatcher(() =>
                    {
                        if (ok)
                        {
                            SyncConnectionStatusText = "✓ Подключение успешно! Доступ к бакету подтверждён.";
                            IsConnectionSuccess = true;
                        }
                        else
                        {
                            SyncConnectionStatusText = "✕ Не удалось подтвердить подключение к бакету.";
                            IsConnectionError = true;
                        }
                    });
                }
                else
                {
                    RunOnDispatcher(() =>
                    {
                        SyncConnectionStatusText = "✕ Проверка подключения недоступна без облачного транспорта.";
                        IsConnectionError = true;
                    });
                }
            }
            catch (OperationCanceledException)
            {
                RunOnDispatcher(() =>
                {
                    SyncConnectionStatusText = UserFacingOperationError.Timeout;
                    IsConnectionWarning = true;
                });
            }
            catch (CloudOfflineException ex)
            {
                RunOnDispatcher(() =>
                {
                    SyncConnectionStatusText = $"⚠ Нет сети: не удалось подключиться к серверу ({ex.Message}).";
                    IsConnectionWarning = true;
                });
            }
            catch (CloudAuthException ex)
            {
                RunOnDispatcher(() =>
                {
                    SyncConnectionStatusText = $"✕ Неверные ключи или права: доступ к бакету запрещён ({ex.Message}).";
                    IsConnectionError = true;
                });
            }
            catch (CloudQuotaException ex)
            {
                RunOnDispatcher(() =>
                {
                    SyncConnectionStatusText = $"⚠ Квота или временный сбой: {ex.Message}";
                    IsConnectionWarning = true;
                });
            }
            catch (CloudStorageException ex) when (ex.StatusCode == 404 || (ex.Message?.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ?? false) || (ex.Message?.Contains("NoSuchBucket", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                RunOnDispatcher(() =>
                {
                    SyncConnectionStatusText = $"✕ Бакет не найден: проверьте имя бакета ({ex.Message}).";
                    IsConnectionError = true;
                });
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("SettingsViewModel.TestConnection", ex);
                RunOnDispatcher(() =>
                {
                    SyncConnectionStatusText = UserFacingOperationError.GenericFailure;
                    IsConnectionError = true;
                });
            }
            finally
            {
                RunOnDispatcher(() =>
                {
                    IsTestingConnection = false;
                    _testConnectionCts?.Dispose();
                    _testConnectionCts = null;
                    RaiseSyncCommandsCanExecute();
                });
            }
        });
    }

    internal async Task SaveAsync()
    {
        try
        {
            await SaveCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SettingsViewModel.Save", ex);
            _showMessage(UserFacingOperationError.GenericFailure, "Ошибка", MessageBoxImage.Error);
        }
    }

    private async Task SaveCoreAsync()
    {
        if (!HotkeyCtrl && !HotkeyShift && !HotkeyAlt && !HotkeyWin)
        {
            _showMessage(
                "Выберите хотя бы одну клавишу-модификатор для открытия редактора (Ctrl, Shift, Alt или Win).",
                "Предупреждение",
                MessageBoxImage.Warning);
            return;
        }

        if (!InstantHotkeyCtrl && !InstantHotkeyShift && !InstantHotkeyAlt && !InstantHotkeyWin)
        {
            _showMessage(
                "Выберите хотя бы одну клавишу-модификатор для мгновенного сохранения (Ctrl, Shift, Alt или Win).",
                "Предупреждение",
                MessageBoxImage.Warning);
            return;
        }

        if (HotkeyCtrl == InstantHotkeyCtrl &&
            HotkeyShift == InstantHotkeyShift &&
            HotkeyAlt == InstantHotkeyAlt &&
            HotkeyWin == InstantHotkeyWin &&
            string.Equals(SelectedKey, SelectedInstantKey, StringComparison.OrdinalIgnoreCase))
        {
            _showMessage(
                "Горячие клавиши для открытия редактора и мгновенного сохранения не должны совпадать.",
                "Конфликт горячих клавиш",
                MessageBoxImage.Warning);
            return;
        }

        if (!ValidateMaxAttachmentSize(out var attachmentSizeError))
        {
            _showMessage(
                attachmentSizeError ?? InvalidMaxAttachmentSizeErrorMessage,
                "Неверный размер вложения",
                MessageBoxImage.Warning);
            return;
        }

        bool syncOk = await SaveSyncSettingsAsync();
        if (!syncOk && SyncEnabled)
        {
            return;
        }

        var current = _settingsService.CurrentSettings;
        var newSettings = new AppSettings
        {
            HotkeyCtrl = HotkeyCtrl,
            HotkeyShift = HotkeyShift,
            HotkeyAlt = HotkeyAlt,
            HotkeyWin = HotkeyWin,
            HotkeyKey = SelectedKey,
            InstantHotkeyCtrl = InstantHotkeyCtrl,
            InstantHotkeyShift = InstantHotkeyShift,
            InstantHotkeyAlt = InstantHotkeyAlt,
            InstantHotkeyWin = InstantHotkeyWin,
            InstantHotkeyKey = SelectedInstantKey,
            OcrHotkeyCtrl = OcrHotkeyCtrl,
            OcrHotkeyShift = OcrHotkeyShift,
            OcrHotkeyAlt = OcrHotkeyAlt,
            OcrHotkeyWin = OcrHotkeyWin,
            OcrHotkeyKey = SelectedOcrKey,
            RunOnStartup = RunOnStartup,
            StartMinimizedToTray = StartMinimizedToTray,
            PreferCloseToTray = PreferCloseToTray,
            CloseToTrayPromptCompleted = true,
            ShowNotificationOnSave = ShowNotificationOnSave,
            LocalRemindersEnabled = LocalRemindersEnabled,
            NavigationPanelWidth = current.NavigationPanelWidth,
            NavigationPanelState = current.NavigationPanelState,
            NoteListPanelWidth = current.NoteListPanelWidth,
            WindowLeft = current.WindowLeft,
            WindowTop = current.WindowTop,
            WindowWidth = current.WindowWidth,
            WindowHeight = current.WindowHeight,
            WindowState = current.WindowState,
            CompactCards = current.CompactCards,
            AutoPurgeTrashEnabled = AutoPurgeTrashEnabled,
            AutoPurgeTrashDays = current.AutoPurgeTrashDays > 0 ? current.AutoPurgeTrashDays : 30,
            Theme = SelectedThemeItem.Theme,
            SortMode = current.SortMode,
            WorkspaceViewMode = current.WorkspaceViewMode,
            SavedWorkspaceViews = current.SavedWorkspaceViews != null
                ? current.SavedWorkspaceViews.Where(v => v != null).Select(v => v.Clone()).ToList()
                : new List<SavedWorkspaceView>(),
            MaxAttachmentSizeMb = MaxAttachmentSizeMb,
            DeviceId = current.DeviceId,
            HasCompletedOnboarding = current.HasCompletedOnboarding,
            LastOpenExportPath = current.LastOpenExportPath,
            LastOpenExportUtc = current.LastOpenExportUtc,
            LastOpenExportCompleteness = current.LastOpenExportCompleteness,
            LastEncryptedArchivePath = current.LastEncryptedArchivePath,
            LastEncryptedArchiveUtc = current.LastEncryptedArchiveUtc,
            CloudSync = current.CloudSync ?? new SyncCloudSettings()
        };

        if (!GlobalHotkeyService.ValidateOcrAgainstEditorReserved(newSettings, out var editorConflict))
        {
            _showMessage(
                editorConflict ?? "Сочетание снимка экрана совпадает с командой редактора.",
                "Конфликт горячих клавиш",
                MessageBoxImage.Warning);
            return;
        }

        // Try registering the new hotkeys immediately
        bool registered = _hotkeyService.Register(newSettings, out var error);
        if (!registered)
        {
            _showMessage(error ?? "Не удалось зарегистрировать горячие клавиши.", "Горячая клавиша не изменена",
                MessageBoxImage.Warning);
            return;
        }
        try
        {
            _settingsService.SaveSettings(newSettings);
            ThemeService.ApplyTheme(newSettings.Theme);

            _taskReminderScheduler?.NotifySettingsChanged();
            RefreshLocalRemindersStatus();

            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            _hotkeyService.Register(_settingsService.CurrentSettings, out var rollbackError);
            _showMessage($"Не удалось сохранить настройки: {ex.Message}\n{rollbackError}", "Ошибка",
                MessageBoxImage.Error);
        }
    }

    private void RefreshLocalRemindersStatus()
    {
        if (!LocalRemindersEnabled)
        {
            LocalRemindersStatusText = "Локальные напоминания: выключены";
            return;
        }

        var status = _taskReminderScheduler?.Status;
        if (status == null)
        {
            LocalRemindersStatusText = "Включены. Напоминание в 09:00 по местному времени, пока приложение открыто.";
            return;
        }

        if (!status.AdapterAvailable)
        {
            LocalRemindersStatusText = status.LastError ?? "Локальные напоминания: недоступны";
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            LocalRemindersStatusText = status.LastError;
            return;
        }

        LocalRemindersStatusText = status.CompactText;
    }
}
