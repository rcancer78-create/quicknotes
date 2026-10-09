using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.ViewModels;

public class ImportExportViewModel : ViewModelBase
{
    private readonly Func<QuickNotesDbContext> _contextFactory;
    private readonly INoteExportService _exportService;
    private readonly INoteImportService _importService;
    private readonly INoteHistoryService _historyService;
    private readonly TagDetectionService _tagDetectionService;
    private readonly INoteArchiveService _archiveService;
    private readonly IAttachmentStorageService _attachmentStorage;
    private readonly SettingsService? _settingsService;
    private readonly IEncryptedArchiveService _encryptedArchiveService;
    private readonly ILocalMutationCoordinator _mutationCoordinator;
    private readonly BoundedOwnership _busyOwnership = new();
    private readonly BoundedOwnership _encryptedArchiveOwnership = new();
    private bool _isClosed;

    private int _selectedTabIndex;
    private int _nonDeletedNotesCount;
    private string _exportStatusMessage = string.Empty;
    private bool _isExportSuccess;
    private string _importStatusMessage = string.Empty;
    private bool _isImportSuccess;
    private ImportPreviewResult? _previewResult;
    private bool _isBusy;
    private int _lastImportedNotesCount;
    private int _lastReplacedNotesCount;
    private int _lastSkippedNotesCount;
    private int _lastCreatedTagsCount;
    private string _lastImportSummary = string.Empty;

    public Func<string, string, string?>? SaveFileDialogProvider { get; set; }
    public Func<string, string[]?>? OpenFileDialogProvider { get; set; }
    public Func<string?>? FolderBrowserDialogProvider { get; set; }
    public Func<string?>? EncryptedArchiveSavePathProvider { get; set; }
    public Func<string?>? EncryptedArchiveOpenPathProvider { get; set; }
    public Func<string?>? RecoveryKeyOfflineSavePathProvider { get; set; }
    public Func<string, bool>? PrintRecoveryKeyDocumentProvider { get; set; }
    public Func<string, string, bool>? EncryptedArchiveConfirmProvider { get; set; }
    public Action<string, string, MessageBoxImage>? MessageBoxProvider { get; set; }
    public Func<string, string, bool>? ConfirmProvider { get; set; }
    public Action<bool?>? CloseAction { get; set; }

    public int ImportedNotesCount { get; private set; }

    public ObservableCollection<ImportItemPreview> PreviewItems { get; } = new();
    public ObservableCollection<ImportDiagnosticItem> Diagnostics { get; } = new();
    public ObservableCollection<ImportFolderMappingEntry> FolderMappings { get; } = new();

    public IReadOnlyList<ImportDuplicateChoice> DuplicateChoices { get; } = ImportDuplicateChoice.All;

    private string _lastOpenExportPath = string.Empty;
    private DateTime? _lastOpenExportUtc;
    private string _lastOpenExportCompleteness = string.Empty;

    public string LastOpenExportPath
    {
        get => _lastOpenExportPath;
        private set
        {
            if (SetProperty(ref _lastOpenExportPath, value))
            {
                OnPropertyChanged(nameof(LastSuccessfulExportBanner));
            }
        }
    }

    public DateTime? LastOpenExportUtc
    {
        get => _lastOpenExportUtc;
        private set
        {
            if (SetProperty(ref _lastOpenExportUtc, value))
            {
                OnPropertyChanged(nameof(HasLastSuccessfulExport));
                OnPropertyChanged(nameof(LastOpenExportUtcDisplay));
                OnPropertyChanged(nameof(LastSuccessfulExportBanner));
            }
        }
    }

    public string LastOpenExportCompleteness
    {
        get => _lastOpenExportCompleteness;
        private set
        {
            if (SetProperty(ref _lastOpenExportCompleteness, value))
            {
                OnPropertyChanged(nameof(LastSuccessfulExportBanner));
            }
        }
    }

    public bool HasLastSuccessfulExport => LastOpenExportUtc.HasValue;
    public string LastOpenExportUtcDisplay => LastOpenExportUtc.HasValue
        ? LastOpenExportUtc.Value.ToString("u")
        : string.Empty;

    public string LastSuccessfulExportBanner => HasLastSuccessfulExport
        ? $"Последний успешный открытый экспорт: {LastOpenExportPath}\nВремя (UTC): {LastOpenExportUtcDisplay}\nПолнота: {LastOpenExportCompleteness}"
        : "Последний успешный открытый экспорт: ещё не выполнялся в этом профиле.";

    private string _encryptedArchiveDestinationPath = string.Empty;
    private string _encryptedArchivePassword = string.Empty;
    private string _encryptedArchivePasswordConfirm = string.Empty;
    private string _shownRecoveryKey = string.Empty;
    private string _recoveryKeyConfirmation = string.Empty;
    private string _encryptedArchiveStatusMessage = string.Empty;
    private bool _isEncryptedArchiveBusy;
    private bool _hasPendingRecoveryVerification;
    private string? _pendingVerificationArchivePath;
    private bool _pendingAfterRotation;
    private string _encryptedArchiveRotatePath = string.Empty;
    private string _encryptedArchiveRotatePassword = string.Empty;
    private string _encryptedArchiveRotateCurrentRecovery = string.Empty;
    private string _lastEncryptedArchivePath = string.Empty;
    private DateTime? _lastEncryptedArchiveUtc;
    private SynchronizationContext? _encryptedArchiveUiContext;

    /// <summary>
    /// Test-only: when set, Create passes this N. Production UI leaves it null (shipping default).
    /// </summary>
    internal int? TestOverridePbkdf2Iterations { get; set; }

    public string EncryptedArchiveDestinationPath
    {
        get => _encryptedArchiveDestinationPath;
        set => SetProperty(ref _encryptedArchiveDestinationPath, value);
    }

    public string ShownRecoveryKey
    {
        get => _shownRecoveryKey;
        private set => SetProperty(ref _shownRecoveryKey, value);
    }

    public string EncryptedArchiveStatusMessage
    {
        get => _encryptedArchiveStatusMessage;
        private set => SetProperty(ref _encryptedArchiveStatusMessage, value);
    }

    public bool IsEncryptedArchiveBusy
    {
        get => _isEncryptedArchiveBusy;
        private set
        {
            if (SetProperty(ref _isEncryptedArchiveBusy, value))
            {
                OnPropertyChanged(nameof(CanEditEncryptedArchiveCreateInputs));
                OnPropertyChanged(nameof(CanEditEncryptedArchiveRecoveryInputs));
                OnPropertyChanged(nameof(CanEditEncryptedArchiveRotateInputs));
                OnPropertyChanged(nameof(CanCloseImportExportWindow));
                RaiseEncryptedArchiveCommandsCanExecuteChanged();
            }
        }
    }

    public bool HasPendingRecoveryVerification
    {
        get => _hasPendingRecoveryVerification;
        private set
        {
            if (SetProperty(ref _hasPendingRecoveryVerification, value))
            {
                OnPropertyChanged(nameof(CanEditEncryptedArchiveCreateInputs));
                OnPropertyChanged(nameof(CanEditEncryptedArchiveRecoveryInputs));
                OnPropertyChanged(nameof(CanEditEncryptedArchiveRotateInputs));
                RaiseEncryptedArchiveCommandsCanExecuteChanged();
            }
        }
    }

    public bool CanEditEncryptedArchiveCreateInputs => !IsEncryptedArchiveBusy && !HasPendingRecoveryVerification;

    public bool CanEditEncryptedArchiveRecoveryInputs => !IsEncryptedArchiveBusy && HasPendingRecoveryVerification;

    public bool CanEditEncryptedArchiveRotateInputs => !IsEncryptedArchiveBusy && !HasPendingRecoveryVerification;

    public bool CanCloseImportExportWindow => !IsEncryptedArchiveBusy && !IsBusy;

    internal TimeSpan ExportTimeout { get; set; } = BoundedOperation.OpenExportTimeout;
    internal TimeSpan ImportTimeout { get; set; } = BoundedOperation.ImportTimeout;
    internal TimeSpan EncryptedArchiveTimeout { get; set; } = BoundedOperation.EncryptedArchiveTimeout;

    public string LastEncryptedArchivePath
    {
        get => _lastEncryptedArchivePath;
        private set
        {
            if (SetProperty(ref _lastEncryptedArchivePath, value))
            {
                OnPropertyChanged(nameof(HasLastSuccessfulEncryptedArchive));
                OnPropertyChanged(nameof(LastSuccessfulEncryptedArchiveBanner));
            }
        }
    }

    public DateTime? LastEncryptedArchiveUtc
    {
        get => _lastEncryptedArchiveUtc;
        private set
        {
            if (SetProperty(ref _lastEncryptedArchiveUtc, value))
            {
                OnPropertyChanged(nameof(HasLastSuccessfulEncryptedArchive));
                OnPropertyChanged(nameof(LastEncryptedArchiveUtcDisplay));
                OnPropertyChanged(nameof(LastSuccessfulEncryptedArchiveBanner));
            }
        }
    }

    public bool HasLastSuccessfulEncryptedArchive => LastEncryptedArchiveUtc.HasValue && !string.IsNullOrWhiteSpace(LastEncryptedArchivePath);

    public string LastEncryptedArchiveUtcDisplay => LastEncryptedArchiveUtc.HasValue
        ? LastEncryptedArchiveUtc.Value.ToString("u")
        : string.Empty;

    public string LastSuccessfulEncryptedArchiveBanner =>
        EncryptedArchiveSetupUi.LastSuccessBanner(
            HasLastSuccessfulEncryptedArchive ? LastEncryptedArchivePath : null,
            LastEncryptedArchiveUtc);

    public string RecoveryKeyDoesNotReplaceNotePasswordText => EncryptedArchiveSetupUi.RecoveryKeyDoesNotReplaceNotePassword;

    public string RecoveryKeyShowOnceWarningText => EncryptedArchiveSetupUi.RecoveryKeyShowOnceWarning;

    public string EncryptedArchiveSetupIncompleteHint => EncryptedArchiveSetupUi.SetupIncompleteHint;

    public string EncryptedArchiveRotateSectionHint => EncryptedArchiveSetupUi.RotateSectionHint;

    public string EncryptedArchiveOfflineCopyHint => EncryptedArchiveSetupUi.OfflineCopySectionHint;

    public string EncryptedArchiveRotatePath
    {
        get => _encryptedArchiveRotatePath;
        set => SetProperty(ref _encryptedArchiveRotatePath, value);
    }

    internal string? PendingVerificationArchivePath => _pendingVerificationArchivePath;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public int NonDeletedNotesCount
    {
        get => _nonDeletedNotesCount;
        set => SetProperty(ref _nonDeletedNotesCount, value);
    }

    public string ExportStatusMessage
    {
        get => _exportStatusMessage;
        set => SetProperty(ref _exportStatusMessage, value);
    }

    public bool IsExportSuccess
    {
        get => _isExportSuccess;
        set => SetProperty(ref _isExportSuccess, value);
    }

    public string ImportStatusMessage
    {
        get => _importStatusMessage;
        set => SetProperty(ref _importStatusMessage, value);
    }

    public bool IsImportSuccess
    {
        get => _isImportSuccess;
        set
        {
            if (SetProperty(ref _isImportSuccess, value))
            {
                OnPropertyChanged(nameof(HasCompletedImport));
                OnPropertyChanged(nameof(ShowImportPreviewChrome));
            }
        }
    }

    public int LastImportedNotesCount
    {
        get => _lastImportedNotesCount;
        private set => SetProperty(ref _lastImportedNotesCount, value);
    }

    public int LastReplacedNotesCount
    {
        get => _lastReplacedNotesCount;
        private set => SetProperty(ref _lastReplacedNotesCount, value);
    }

    public int LastSkippedNotesCount
    {
        get => _lastSkippedNotesCount;
        private set => SetProperty(ref _lastSkippedNotesCount, value);
    }

    public int LastCreatedTagsCount
    {
        get => _lastCreatedTagsCount;
        private set => SetProperty(ref _lastCreatedTagsCount, value);
    }

    public string LastImportSummary
    {
        get => _lastImportSummary;
        private set => SetProperty(ref _lastImportSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCloseImportExportWindow));
                OnPropertyChanged(nameof(CanConfirmImport));
                (CloseCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ConfirmImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ImportPreviewResult? PreviewResult
    {
        get => _previewResult;
        private set
        {
            if (SetProperty(ref _previewResult, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(CanConfirmImport));
                OnPropertyChanged(nameof(TotalFilesDiscovered));
                OnPropertyChanged(nameof(TotalNotesToImport));
                OnPropertyChanged(nameof(TotalConflicts));
                OnPropertyChanged(nameof(TotalSkippedOrErroneous));
                OnPropertyChanged(nameof(NewTagsCount));
                OnPropertyChanged(nameof(ConflictPolicyDescription));
                OnPropertyChanged(nameof(HasDiagnostics));
                OnPropertyChanged(nameof(HasFolderMapping));
                OnPropertyChanged(nameof(HasNonBlockingLosses));
                OnPropertyChanged(nameof(HasBlockingDiagnostics));
                OnPropertyChanged(nameof(CanAcknowledgeLosses));
                OnPropertyChanged(nameof(HasCompletedImport));
                OnPropertyChanged(nameof(ShowImportPreviewChrome));
            }
        }
    }

    public bool HasPreview => PreviewResult != null;
    public bool HasCompletedImport => _isImportSuccess && _previewResult == null;
    public bool ShowImportPreviewChrome => !HasCompletedImport;
    public bool CanConfirmImport => PreviewResult != null && PreviewResult.CanConfirm && !IsBusy && !HasCompletedImport;
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public bool HasFolderMapping => FolderMappings.Count > 0;
    public bool HasNonBlockingLosses => PreviewResult?.HasNonBlockingLosses == true;
    public bool HasBlockingDiagnostics => PreviewResult?.HasBlockingDiagnostics == true;
    public bool CanAcknowledgeLosses => HasNonBlockingLosses && !IsBusy;

    public bool LossesAcknowledged
    {
        get => PreviewResult?.LossesAcknowledged == true;
        set
        {
            if (PreviewResult == null || PreviewResult.LossesAcknowledged == value)
            {
                return;
            }

            PreviewResult.LossesAcknowledged = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmImport));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int TotalFilesDiscovered => PreviewResult?.TotalFilesDiscovered ?? 0;
    public int TotalNotesToImport => PreviewResult?.TotalNotesToImport ?? 0;
    public int TotalConflicts => PreviewResult?.TotalConflicts ?? 0;
    public int TotalSkippedOrErroneous => PreviewResult?.TotalSkippedOrErroneous ?? 0;
    public int NewTagsCount => PreviewResult?.NewTagsCount ?? 0;
    public string ConflictPolicyDescription => PreviewResult?.ConflictPolicyDescription ??
        "Политика при конфликтах: пропускать существующие заметки (без перезаписи)";

    public ICommand ExportMarkdownCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportArchiveCommand { get; }

    public ICommand BrowseEncryptedArchivePathCommand { get; }
    public ICommand CreateEncryptedArchiveCommand { get; }
    public ICommand VerifyEncryptedArchiveRecoveryCommand { get; }
    public ICommand CancelEncryptedArchiveSetupCommand { get; }
    public ICommand SaveRecoveryKeyOfflineCopyCommand { get; }
    public ICommand PrintRecoveryKeyCommand { get; }
    public ICommand BrowseEncryptedArchiveRotatePathCommand { get; }
    public ICommand RotateEncryptedArchiveRecoveryCommand { get; }

    public ICommand SelectFilesCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand ConfirmImportCommand { get; }
    public ICommand CloseCommand { get; }

    internal ILocalMutationCoordinator MutationCoordinatorForTests => _mutationCoordinator;

    public ImportExportViewModel(
        Func<QuickNotesDbContext> contextFactory,
        ILocalMutationCoordinator mutationCoordinator,
        INoteExportService? exportService = null,
        INoteImportService? importService = null,
        INoteHistoryService? historyService = null,
        TagDetectionService? tagDetectionService = null,
        INoteArchiveService? archiveService = null,
        IAttachmentStorageService? attachmentStorage = null,
        SettingsService? settingsService = null,
        IEncryptedArchiveService? encryptedArchiveService = null)
    {
        ArgumentNullException.ThrowIfNull(mutationCoordinator);
        _contextFactory = contextFactory;
        _mutationCoordinator = mutationCoordinator;
        _exportService = exportService ?? new NoteExportService();
        _importService = importService ?? new NoteImportService();
        _historyService = historyService ?? new NoteHistoryService();
        _tagDetectionService = tagDetectionService ?? new TagDetectionService();
        _archiveService = archiveService ?? new NoteArchiveService();
        _attachmentStorage = attachmentStorage ?? new AttachmentStorageService();
        _settingsService = settingsService;
        _encryptedArchiveService = encryptedArchiveService ?? new EncryptedArchiveService();

        _busyOwnership.DrainCompleted += () => ApplyEncryptedArchiveUi(() =>
        {
            if (_isClosed)
            {
                return;
            }

            IsBusy = false;
        });
        _encryptedArchiveOwnership.DrainCompleted += () => ApplyEncryptedArchiveUi(() =>
        {
            if (_isClosed)
            {
                return;
            }

            IsEncryptedArchiveBusy = false;
        });

        // Initialize default dialog providers
        SaveFileDialogProvider = DefaultSaveFileDialog;
        OpenFileDialogProvider = DefaultOpenFileDialog;
        FolderBrowserDialogProvider = DefaultFolderBrowserDialog;
        MessageBoxProvider = (msg, title, img) => System.Windows.MessageBox.Show(msg, title, MessageBoxButton.OK, img);
        ConfirmProvider = (msg, title) =>
            System.Windows.MessageBox.Show(msg, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        ExportMarkdownCommand = new RelayCommand(ExecuteExportMarkdownAsync, () => !IsBusy);
        ExportJsonCommand = new RelayCommand(ExecuteExportJsonAsync, () => !IsBusy);
        ExportCsvCommand = new RelayCommand(ExecuteExportCsvAsync, () => !IsBusy);
        ExportArchiveCommand = new RelayCommand(ExecuteExportArchiveAsync, () => !IsBusy);

        BrowseEncryptedArchivePathCommand = new RelayCommand(_ => BrowseEncryptedArchivePath(), _ => CanEditEncryptedArchiveCreateInputs);
        CreateEncryptedArchiveCommand = new RelayCommand(
            _ => FireEncryptedArchiveCommand(ExecuteCreateEncryptedArchiveAsync),
            _ => CanEditEncryptedArchiveCreateInputs);
        VerifyEncryptedArchiveRecoveryCommand = new RelayCommand(
            _ => FireEncryptedArchiveCommand(ExecuteVerifyEncryptedArchiveRecoveryAsync),
            _ => CanEditEncryptedArchiveRecoveryInputs);
        CancelEncryptedArchiveSetupCommand = new RelayCommand(_ => CancelEncryptedArchiveSetup(), _ => CanEditEncryptedArchiveRecoveryInputs);
        SaveRecoveryKeyOfflineCopyCommand = new RelayCommand(
            _ => FireEncryptedArchiveCommand(ExecuteSaveRecoveryKeyOfflineCopyAsync),
            _ => CanEditEncryptedArchiveRecoveryInputs);
        PrintRecoveryKeyCommand = new RelayCommand(
            _ => PrintShownRecoveryKey(),
            _ => CanEditEncryptedArchiveRecoveryInputs);
        BrowseEncryptedArchiveRotatePathCommand = new RelayCommand(_ => BrowseEncryptedArchiveRotatePath(), _ => CanEditEncryptedArchiveRotateInputs);
        RotateEncryptedArchiveRecoveryCommand = new RelayCommand(
            _ => FireEncryptedArchiveCommand(ExecuteRotateEncryptedArchiveRecoveryAsync),
            _ => CanEditEncryptedArchiveRotateInputs);

        SelectFilesCommand = new RelayCommand(ExecuteSelectFilesAsync, () => !IsBusy);
        SelectFolderCommand = new RelayCommand(ExecuteSelectFolderAsync, () => !IsBusy);
        ConfirmImportCommand = new RelayCommand(ExecuteConfirmImportAsync, () => CanConfirmImport);
        CloseCommand = new RelayCommand(_ => ExecuteClose(), _ => CanCloseImportExportWindow);

        EncryptedArchiveSavePathProvider = DefaultEncryptedArchiveSaveDialog;
        EncryptedArchiveOpenPathProvider = DefaultEncryptedArchiveOpenDialog;
        RecoveryKeyOfflineSavePathProvider = DefaultRecoveryKeyOfflineSaveDialog;
        PrintRecoveryKeyDocumentProvider = DefaultPrintRecoveryKeyDocument;

        LoadLastSuccessfulExport();
        LoadLastSuccessfulEncryptedArchive();
        RefreshNoteCount();
    }

    public void RefreshNoteCount()
    {
        try
        {
            using var db = _contextFactory();
            NonDeletedNotesCount = db.Notes.Count(n => n.DeletedAt == null);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("ImportExportViewModel.RefreshNoteCount", $"{ex.GetType().Name}: {ex.Message}");
            NonDeletedNotesCount = 0;
        }
    }

    public const string OpenExportPlaintextWarning =
        "Открытый экспорт запишет незащищённые заметки, заголовки (первая строка текста), теги, ссылки, метаданные источника и доступные вложения в выбранную папку открытым текстом (plaintext). Защищённые заметки не войдут в выгрузку. Любой, у кого есть доступ к папке, сможет прочитать файлы. Это не зашифрованный архив восстановления и не процедура восстановления базы QuickNotes. Продолжить?";

    public Task ExecuteExportMarkdownAsync()
    {
        var folder = FolderBrowserDialogProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(folder))
            return Task.CompletedTask;

        if (ConfirmProvider != null && !ConfirmProvider(OpenExportPlaintextWarning, "Открытый экспорт — plaintext"))
            return Task.CompletedTask;

        return RunBoundedExportAsync(
            "ImportExportViewModel.ExecuteExportMarkdown",
            ct =>
            {
                using var db = _contextFactory();
                return _exportService.ExportToMarkdown(db, folder, _attachmentStorage, ct);
            },
            res => ApplyExportResult(res, folder, "Экспорт в Markdown", persistAsLastOpenExport: true));
    }

    public async Task ExecuteExportJsonAsync()
    {
        var filePath = SaveFileDialogProvider?.Invoke("QuickNotes JSON (*.json)|*.json", "json");
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        await RunBoundedExportAsync(
            "ImportExportViewModel.ExecuteExportJson",
            ct =>
            {
                using var db = _contextFactory();
                return _exportService.ExportToJson(db, filePath, ct);
            },
            res => ApplySimpleOpenExport(res, filePath, "Экспорт в JSON")).ConfigureAwait(true);
    }

    public async Task ExecuteExportArchiveAsync()
    {
        var filePath = SaveFileDialogProvider?.Invoke("Архив QuickNotes (*.qnarchive.zip)|*.qnarchive.zip|ZIP (*.zip)|*.zip", "zip");
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        await RunBoundedExportAsync(
            "ImportExportViewModel.ExecuteExportArchive",
            ct =>
            {
                using var db = _contextFactory();
                return _archiveService.ExportArchive(db, filePath, _attachmentStorage, ct);
            },
            res =>
            {
                if (res.Success)
                {
                    IsExportSuccess = true;
                    ExportStatusMessage = $"ZIP с незащищёнными заметками и доступными вложениями сохранён: {Path.GetFileName(filePath)} ({res.ExportedNotesCount} заметок). Это plaintext, не зашифрованный recovery-архив.";
                    if (res.SkippedProtectedCount > 0)
                    {
                        ExportStatusMessage += $"\nПропущено защищённых заметок: {res.SkippedProtectedCount} (защищённые заметки не экспортируются в открытом виде).";
                    }
                    MessageBoxProvider?.Invoke(ExportStatusMessage, "Экспорт архива", MessageBoxImage.Information);
                }
                else
                {
                    IsExportSuccess = false;
                    ExportStatusMessage = res.ErrorMessage ?? "Ошибка экспорта архива";
                    MessageBoxProvider?.Invoke(ExportStatusMessage, "Ошибка экспорта", MessageBoxImage.Warning);
                }
            }).ConfigureAwait(true);
    }

    public async Task ExecuteExportCsvAsync()
    {
        var filePath = SaveFileDialogProvider?.Invoke("CSV (Разделители-запятые) (*.csv)|*.csv", "csv");
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        await RunBoundedExportAsync(
            "ImportExportViewModel.ExecuteExportCsv",
            ct =>
            {
                using var db = _contextFactory();
                return _exportService.ExportToCsv(db, filePath, ct);
            },
            res => ApplySimpleOpenExport(res, filePath, "Экспорт в CSV")).ConfigureAwait(true);
    }

    private async Task RunBoundedExportAsync(string logScope, Func<CancellationToken, ExportResult> export, Action<ExportResult> applySuccess)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var bounded = await BoundedOperation.RunAsync(
                ct => Task.Run(() => export(ct), ct),
                ExportTimeout,
                CancellationToken.None,
                logScope,
                BoundedTimeoutBehavior.WaitForOwnedWork,
                _busyOwnership).ConfigureAwait(true);

            if (bounded.Outcome == BoundedOperationOutcome.Success && bounded.Value != null)
            {
                applySuccess(bounded.Value);
                return;
            }

            IsExportSuccess = false;
            ExportStatusMessage = bounded.UserMessage;
            MessageBoxProvider?.Invoke(ExportStatusMessage, "Экспорт", MessageBoxImage.Warning);
        }
        finally
        {
            if (!_busyOwnership.IsCancelling)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplySimpleOpenExport(ExportResult res, string filePath, string title)
    {
        if (res.Success)
        {
            IsExportSuccess = true;
            ExportStatusMessage = $"Успешно экспортировано {res.ExportedNotesCount} заметок в файл: {Path.GetFileName(filePath)}";
            if (res.SkippedProtectedCount > 0)
            {
                ExportStatusMessage += $"\nПропущено защищённых заметок: {res.SkippedProtectedCount} (защищённые заметки не экспортируются в открытом виде).";
            }
            MessageBoxProvider?.Invoke(ExportStatusMessage, title, MessageBoxImage.Information);
        }
        else
        {
            IsExportSuccess = false;
            ExportStatusMessage = res.ErrorMessage ?? "Ошибка экспорта";
            MessageBoxProvider?.Invoke(ExportStatusMessage, "Ошибка экспорта", MessageBoxImage.Warning);
        }
    }

    public Task ExecuteSelectFilesAsync()
    {
        var filter = "Все поддерживаемые (*.md;*.txt;*.html;*.htm;*.json;*.zip)|*.md;*.txt;*.html;*.htm;*.json;*.zip|Архив QuickNotes (*.qnarchive.zip;*.zip)|*.qnarchive.zip;*.zip|Markdown (*.md)|*.md|HTML (*.html;*.htm)|*.html;*.htm|Текстовые файлы (*.txt)|*.txt|QuickNotes JSON (*.json)|*.json|Все файлы (*.*)|*.*";
        var files = OpenFileDialogProvider?.Invoke(filter);
        if (files == null || files.Length == 0)
            return Task.CompletedTask;

        if (files.Length == 1 && IsArchiveFile(files[0]))
        {
            return LoadPreviewAsync(ct =>
            {
                using var db = _contextFactory();
                return _archiveService.PreviewArchive(db, files[0]);
            });
        }

        return LoadPreviewAsync(ct =>
        {
            using var db = _contextFactory();
            return _importService.BuildPreviewFromFiles(db, files, ct);
        });
    }

    public Task ExecuteSelectFolderAsync()
    {
        var folder = FolderBrowserDialogProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(folder))
            return Task.CompletedTask;

        return LoadPreviewAsync(ct =>
        {
            using var db = _contextFactory();
            return _importService.BuildPreviewFromDirectory(db, folder, recursive: true, ct);
        });
    }

    public void LoadPreview(Func<ImportPreviewResult> previewLoader)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ImportStatusMessage = "Анализ файлов...";
        IsImportSuccess = false;
        LastImportSummary = string.Empty;
        try
        {
            ApplyPreviewResult(previewLoader());
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("ImportExportViewModel.LoadPreview", ex);
            ImportStatusMessage = UserFacingOperationError.GenericFailure;
            MessageBoxProvider?.Invoke(ImportStatusMessage, "Ошибка анализа файлов", MessageBoxImage.Error);
        }
        finally
        {
            _busyOwnership.Release();
            IsBusy = false;
        }
    }

    public async Task LoadPreviewAsync(Func<CancellationToken, ImportPreviewResult> previewLoader)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ImportStatusMessage = "Анализ файлов...";
        IsImportSuccess = false;
        LastImportSummary = string.Empty;
        try
        {
            var bounded = await BoundedOperation.RunAsync(
                ct => Task.Run(() => previewLoader(ct), ct),
                ImportTimeout,
                CancellationToken.None,
                "ImportExportViewModel.LoadPreview",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                _busyOwnership).ConfigureAwait(true);

            if (bounded.Outcome != BoundedOperationOutcome.Success || bounded.Value == null)
            {
                ImportStatusMessage = bounded.UserMessage;
                MessageBoxProvider?.Invoke(ImportStatusMessage, "Анализ файлов", MessageBoxImage.Warning);
                return;
            }

            ApplyPreviewResult(bounded.Value);
        }
        finally
        {
            if (!_busyOwnership.IsCancelling)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyPreviewResult(ImportPreviewResult preview)
    {
        PreviewResult = preview;

        PreviewItems.Clear();
        foreach (var item in preview.Items)
        {
            PreviewItems.Add(item);
        }

        Diagnostics.Clear();
        foreach (var diag in preview.Diagnostics)
        {
            Diagnostics.Add(diag);
        }

        FolderMappings.Clear();
        foreach (var map in preview.FolderMappings)
        {
            FolderMappings.Add(map);
        }

        OnPropertyChanged(nameof(HasFolderMapping));
        OnPropertyChanged(nameof(HasNonBlockingLosses));
        OnPropertyChanged(nameof(HasBlockingDiagnostics));
        OnPropertyChanged(nameof(LossesAcknowledged));
        CommandManager.InvalidateRequerySuggested();

        if (preview.TotalNotesToImport == 0)
        {
            if (preview.TotalConflicts > 0)
            {
                ImportStatusMessage = "Все обнаруженные заметки уже существуют в базе данных (пропущены согласно политике конфликтов).";
            }
            else if (preview.TotalSkippedOrErroneous > 0)
            {
                ImportStatusMessage = "В выбранных файлах не найдено корректных заметок (см. диагностику ошибок ниже).";
            }
            else
            {
                ImportStatusMessage = "Подходящих файлов для импорта не обнаружено.";
            }
        }
        else
        {
            ImportStatusMessage = $"Готово к импорту: {preview.TotalNotesToImport} заметок. Новых тегов: {preview.NewTagsCount}. Конфликтов (пропуск): {preview.TotalConflicts}.";
        }
    }

    public void ExecuteConfirmImport()
    {
        if (PreviewResult == null || !PreviewResult.CanConfirm || IsBusy || !_busyOwnership.TryAcquire())
        {
            return;
        }

        IsBusy = true;
        try
        {
            ApplyConfirmImport(ConfirmImportCore(PreviewResult, CancellationToken.None));
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("ImportExportViewModel.ExecuteConfirmImport", ex);
            IsImportSuccess = false;
            ImportStatusMessage = UserFacingOperationError.GenericFailure;
            MessageBoxProvider?.Invoke(ImportStatusMessage, "Критическая ошибка импорта", MessageBoxImage.Error);
        }
        finally
        {
            _busyOwnership.Release();
            IsBusy = false;
        }
    }

    public Task ExecuteConfirmImportAsync()
    {
        if (PreviewResult == null || !PreviewResult.CanConfirm || IsBusy)
            return Task.CompletedTask;

        var preview = PreviewResult;
        IsBusy = true;
        return ConfirmImportCoreAsync(preview);
    }

    private ImportExecutionResult ConfirmImportCore(ImportPreviewResult preview, CancellationToken cancellationToken)
    {
        using var db = _contextFactory();
        return preview.IsPortableArchive
            ? _archiveService.ImportArchive(db, preview, _attachmentStorage, _mutationCoordinator, _historyService, cancellationToken)
            : _importService.ExecuteImport(db, preview, _mutationCoordinator, _historyService, _tagDetectionService, _attachmentStorage, cancellationToken);
    }

    private async Task ConfirmImportCoreAsync(ImportPreviewResult preview)
    {
        try
        {
            var bounded = await BoundedOperation.RunAsync(
                ct => Task.Run(() => ConfirmImportCore(preview, ct), ct),
                ImportTimeout,
                CancellationToken.None,
                "ImportExportViewModel.ExecuteConfirmImport",
                BoundedTimeoutBehavior.WaitForOwnedWork,
                _busyOwnership).ConfigureAwait(true);

            if (bounded.Outcome != BoundedOperationOutcome.Success || bounded.Value == null)
            {
                IsImportSuccess = false;
                ImportStatusMessage = bounded.UserMessage;
                MessageBoxProvider?.Invoke(ImportStatusMessage, "Импорт", MessageBoxImage.Warning);
                return;
            }

            ApplyConfirmImport(bounded.Value);
        }
        finally
        {
            if (!_busyOwnership.IsCancelling)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyConfirmImport(ImportExecutionResult res)
    {
        if (res.Success)
        {
            ImportedNotesCount += res.ImportedNotesCount;
            LastImportedNotesCount = res.ImportedNotesCount;
            LastReplacedNotesCount = res.ReplacedNotesCount;
            LastSkippedNotesCount = res.SkippedNotesCount;
            LastCreatedTagsCount = res.CreatedTagsCount;
            LastImportSummary = string.IsNullOrWhiteSpace(res.Summary)
                ? $"Импортировано новых: {res.ImportedNotesCount}. Заменено: {res.ReplacedNotesCount}. Пропущено: {res.SkippedNotesCount}. Новых тегов: {res.CreatedTagsCount}."
                : res.Summary;
            IsImportSuccess = true;
            ImportStatusMessage = LastImportSummary;
            MessageBoxProvider?.Invoke(ImportStatusMessage, "Импорт завершён", MessageBoxImage.Information);
            RefreshNoteCount();

            PreviewResult = null;
            PreviewItems.Clear();
            Diagnostics.Clear();
            FolderMappings.Clear();
            OnPropertyChanged(nameof(HasFolderMapping));
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(HasCompletedImport));
            OnPropertyChanged(nameof(ShowImportPreviewChrome));
        }
        else
        {
            IsImportSuccess = false;
            ImportStatusMessage = res.ErrorMessage ?? "Ошибка при импорте";
            MessageBoxProvider?.Invoke(ImportStatusMessage, "Ошибка импорта", MessageBoxImage.Error);
        }
    }

    public void ApplyFolderMappingEdits()
    {
        if (PreviewResult == null || string.IsNullOrWhiteSpace(PreviewResult.ImportRootPath))
        {
            return;
        }

        ImportFolderMapper.ApplyMappingToItems(PreviewResult.Items, FolderMappings.ToList(), PreviewResult.ImportRootPath);
        using var db = _contextFactory();
        _importService.RecalculatePreviewTotals(db, PreviewResult);
        RefreshPreviewBindings();
    }

    public void ChangeDuplicateAction(ImportItemPreview item, ImportDuplicateAction action)
    {
        if (action == ImportDuplicateAction.Replace && !item.CanReplace)
        {
            ImportStatusMessage = "Замена недоступна: нет безопасного совпадения источника или заметка защищена.";
            return;
        }

        item.DuplicateAction = action;
        item.Status = action switch
        {
            ImportDuplicateAction.ImportSeparate => "Импортировать отдельно",
            ImportDuplicateAction.Replace => "Заменить существующую",
            _ => "Конфликт (пропуск)"
        };
        if (PreviewResult != null)
        {
            using var db = _contextFactory();
            _importService.RecalculatePreviewTotals(db, PreviewResult);
        }

        RefreshPreviewBindings();
    }

    private void RefreshPreviewBindings()
    {
        OnPropertyChanged(nameof(CanConfirmImport));
        OnPropertyChanged(nameof(TotalNotesToImport));
        OnPropertyChanged(nameof(TotalConflicts));
        OnPropertyChanged(nameof(NewTagsCount));
        OnPropertyChanged(nameof(HasNonBlockingLosses));
        OnPropertyChanged(nameof(HasBlockingDiagnostics));
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool IsArchiveFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".qnarchive.zip", StringComparison.OrdinalIgnoreCase)
               || Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DefaultSaveFileDialog(string filter, string defaultExt)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            DefaultExt = defaultExt,
            OverwritePrompt = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string[]? DefaultOpenFileDialog(string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = filter,
            Multiselect = true
        };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    private static string? DefaultFolderBrowserDialog()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для импорта/экспорта заметок",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    private void ApplyExportResult(ExportResult res, string path, string successTitle, bool persistAsLastOpenExport)
    {
        if (res.Success)
        {
            IsExportSuccess = true;
            ExportStatusMessage = $"Успешно экспортировано {res.ExportedNotesCount} заметок: {path}";
            if (!string.IsNullOrWhiteSpace(res.CompletenessSummary))
            {
                ExportStatusMessage += $"\nПолнота: {res.CompletenessSummary}";
            }

            if (res.SkippedProtectedCount > 0)
            {
                ExportStatusMessage += $"\nПропущено защищённых заметок: {res.SkippedProtectedCount} (защищённые заметки не экспортируются в открытом виде).";
            }

            if (persistAsLastOpenExport)
            {
                RememberSuccessfulOpenExport(path, res);
            }

            MessageBoxProvider?.Invoke(ExportStatusMessage, successTitle, MessageBoxImage.Information);
        }
        else
        {
            IsExportSuccess = false;
            ExportStatusMessage = res.ErrorMessage ?? "Ошибка экспорта";
            MessageBoxProvider?.Invoke(ExportStatusMessage, "Ошибка экспорта", MessageBoxImage.Warning);
        }
    }

    private void LoadLastSuccessfulExport()
    {
        var settings = _settingsService?.CurrentSettings;
        if (settings == null || string.IsNullOrWhiteSpace(settings.LastOpenExportPath) || !settings.LastOpenExportUtc.HasValue)
        {
            return;
        }

        LastOpenExportPath = settings.LastOpenExportPath;
        LastOpenExportUtc = settings.LastOpenExportUtc;
        LastOpenExportCompleteness = settings.LastOpenExportCompleteness ?? string.Empty;
        OnPropertyChanged(nameof(LastOpenExportPath));
        OnPropertyChanged(nameof(LastOpenExportUtc));
        OnPropertyChanged(nameof(LastOpenExportUtcDisplay));
        OnPropertyChanged(nameof(LastOpenExportCompleteness));
        OnPropertyChanged(nameof(HasLastSuccessfulExport));
        OnPropertyChanged(nameof(LastSuccessfulExportBanner));
    }

    private void RememberSuccessfulOpenExport(string path, ExportResult res)
    {
        LastOpenExportPath = path;
        LastOpenExportUtc = res.ExportedAtUtc ?? DateTime.UtcNow;
        LastOpenExportCompleteness = res.CompletenessSummary ?? string.Empty;
        OnPropertyChanged(nameof(LastOpenExportPath));
        OnPropertyChanged(nameof(LastOpenExportUtc));
        OnPropertyChanged(nameof(LastOpenExportUtcDisplay));
        OnPropertyChanged(nameof(LastOpenExportCompleteness));
        OnPropertyChanged(nameof(HasLastSuccessfulExport));
        OnPropertyChanged(nameof(LastSuccessfulExportBanner));

        if (_settingsService == null)
        {
            return;
        }

        var settings = _settingsService.CurrentSettings;
        settings.LastOpenExportPath = path;
        settings.LastOpenExportUtc = LastOpenExportUtc;
        settings.LastOpenExportCompleteness = LastOpenExportCompleteness;
        _settingsService.SaveSettings(settings);
    }

    public void SetEncryptedArchivePasswordInput(string? password)
        => _encryptedArchivePassword = password ?? string.Empty;

    public void SetEncryptedArchivePasswordConfirmInput(string? password)
        => _encryptedArchivePasswordConfirm = password ?? string.Empty;

    public void SetRecoveryKeyConfirmationInput(string? recoveryKey)
        => _recoveryKeyConfirmation = recoveryKey ?? string.Empty;

    public void SetEncryptedArchiveRotatePasswordInput(string? password)
        => _encryptedArchiveRotatePassword = password ?? string.Empty;

    public void SetEncryptedArchiveRotateRecoveryInput(string? recoveryKey)
        => _encryptedArchiveRotateCurrentRecovery = recoveryKey ?? string.Empty;

    public void BrowseEncryptedArchivePath()
    {
        if (IsEncryptedArchiveBusy || HasPendingRecoveryVerification)
        {
            return;
        }

        string? path = EncryptedArchiveSavePathProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EncryptedArchiveDestinationPath = path;
    }

    public void BrowseEncryptedArchiveRotatePath()
    {
        if (IsEncryptedArchiveBusy || HasPendingRecoveryVerification)
        {
            return;
        }

        string? path = EncryptedArchiveOpenPathProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EncryptedArchiveRotatePath = path;
    }

    public void PrintShownRecoveryKey()
    {
        if (IsEncryptedArchiveBusy || !HasPendingRecoveryVerification || string.IsNullOrWhiteSpace(ShownRecoveryKey))
        {
            return;
        }

        if (!ConfirmEncryptedArchive(EncryptedArchiveSetupUi.OfflinePrintConfirm, "Печать recovery key"))
        {
            EncryptedArchiveStatusMessage = "Печать отменена.";
            return;
        }

        Guid? archiveId = null;
        string document = RecoveryKeyOfflineCopy.BuildDocument(ShownRecoveryKey, archiveId);
        bool printed = PrintRecoveryKeyDocumentProvider?.Invoke(document) == true;
        EncryptedArchiveStatusMessage = printed
            ? "Recovery key отправлен на печать. Храните распечатку отдельно от .qnar."
            : "Печать не выполнена.";
    }

    public async Task ExecuteSaveRecoveryKeyOfflineCopyAsync()
    {
        if (IsEncryptedArchiveBusy || !HasPendingRecoveryVerification || string.IsNullOrWhiteSpace(ShownRecoveryKey))
        {
            return;
        }

        if (!ConfirmEncryptedArchive(EncryptedArchiveSetupUi.OfflineCopyConfirm, "Офлайн-копия recovery key"))
        {
            EncryptedArchiveStatusMessage = "Сохранение офлайн-копии отменено.";
            return;
        }

        string? path = RecoveryKeyOfflineSavePathProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
        {
            EncryptedArchiveStatusMessage = "Выберите новый файл офлайн-копии (не в каталоге .qnar).";
            return;
        }

        string key = ShownRecoveryKey;
        string? archivePath = _pendingVerificationArchivePath;
        CaptureEncryptedArchiveUiContext();
        IsEncryptedArchiveBusy = true;
        EncryptedArchiveStatusMessage = "Запись офлайн-копии…";

        BoundedOperationResult<bool> bounded = await BoundedOperation.RunAsync(
            ct => Task.Run(() =>
            {
                RecoveryKeyOfflineCopy.WriteNewFile(path, key, archivePath, archiveId: null, ct);
                return true;
            }, ct),
            EncryptedArchiveTimeout,
            CancellationToken.None,
            "ImportExportViewModel.SaveRecoveryKeyOfflineCopy",
            BoundedTimeoutBehavior.WaitForOwnedWork,
            _encryptedArchiveOwnership).ConfigureAwait(false);

        ApplyEncryptedArchiveUi(() =>
        {
            try
            {
                if (bounded.Outcome != BoundedOperationOutcome.Success)
                {
                    EncryptedArchiveStatusMessage = bounded.UserMessage;
                    return;
                }

                EncryptedArchiveStatusMessage = "Офлайн-копия записана в новый файл. Ключ в настройки и журнал не попал.";
            }
            finally
            {
                if (!_encryptedArchiveOwnership.IsCancelling)
                {
                    IsEncryptedArchiveBusy = false;
                }
            }
        });
    }

    public async Task ExecuteRotateEncryptedArchiveRecoveryAsync()
    {
        if (IsEncryptedArchiveBusy || HasPendingRecoveryVerification)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EncryptedArchiveRotatePath))
        {
            BrowseEncryptedArchiveRotatePath();
            if (string.IsNullOrWhiteSpace(EncryptedArchiveRotatePath))
            {
                EncryptedArchiveStatusMessage = "Выберите существующий файл .qnar для ротации recovery key.";
                return;
            }
        }

        bool hasPassword = !string.IsNullOrEmpty(_encryptedArchiveRotatePassword);
        bool hasRecovery = !string.IsNullOrWhiteSpace(_encryptedArchiveRotateCurrentRecovery);
        if (hasPassword == hasRecovery)
        {
            EncryptedArchiveStatusMessage = "Для ротации введите ровно один секрет: текущий recovery key или пароль архива.";
            return;
        }

        if (!ConfirmEncryptedArchive(EncryptedArchiveSetupUi.RotateConfirm, "Ротация recovery key"))
        {
            EncryptedArchiveStatusMessage = "Ротация отменена. Файл не изменён.";
            return;
        }

        string archivePath = EncryptedArchiveRotatePath.Trim();
        string? password = hasPassword ? _encryptedArchiveRotatePassword : null;
        string? currentKey = hasRecovery ? _encryptedArchiveRotateCurrentRecovery : null;
        CaptureEncryptedArchiveUiContext();
        IsEncryptedArchiveBusy = true;
        EncryptedArchiveStatusMessage = "Ротация recovery key…";

        EncryptedArchiveRotateRecoveryResult? rotated = null;
        var bounded = await BoundedOperation.RunAsync(
            ct => Task.Run(() => _encryptedArchiveService.RotateRecovery(new EncryptedArchiveRotateRecoveryRequest
            {
                ArchivePath = archivePath,
                ArchivePassword = password,
                CurrentRecoveryKeyFormatted = currentKey
            }, ct), ct),
            EncryptedArchiveTimeout,
            CancellationToken.None,
            "ImportExportViewModel.RotateEncryptedArchiveRecovery",
            BoundedTimeoutBehavior.WaitForOwnedWork,
            _encryptedArchiveOwnership).ConfigureAwait(false);

        ApplyEncryptedArchiveUi(() =>
        {
            try
            {
                WipeRotateSecretBuffers();
                if (bounded.Outcome != BoundedOperationOutcome.Success || bounded.Value == null)
                {
                    EncryptedArchiveStatusMessage = bounded.UserMessage;
                    return;
                }

                rotated = bounded.Value;

                ShownRecoveryKey = rotated.RecoveryKeyFormatted;
                _pendingVerificationArchivePath = rotated.ArchivePath;
                _pendingAfterRotation = true;
                EncryptedArchiveRotatePath = rotated.ArchivePath;
                EncryptedArchiveDestinationPath = rotated.ArchivePath;
                HasPendingRecoveryVerification = true;
                EncryptedArchiveStatusMessage =
                    "Ротация записана. Старый recovery key этот файл больше не открывает. Сохраните новый ключ отдельно и введите его ниже для контрольного восстановления. Копии старого файла, если остались, старым ключом всё ещё открываются.";
            }
            finally
            {
                if (!_encryptedArchiveOwnership.IsCancelling)
                {
                    IsEncryptedArchiveBusy = false;
                }
            }
        });
    }

    private void FireEncryptedArchiveCommand(Func<Task> operation)
    {
        AsyncEventBridge.Fire(
            operation,
            "ImportExport.EncryptedArchive",
            RestoreEncryptedArchiveCommandState);
    }

    private void RestoreEncryptedArchiveCommandState()
    {
        ApplyEncryptedArchiveUi(() =>
        {
            if (_isClosed || _encryptedArchiveOwnership.IsHeld)
            {
                return;
            }

            IsEncryptedArchiveBusy = false;
        });
    }

    public async Task ExecuteCreateEncryptedArchiveAsync()
    {
        if (IsEncryptedArchiveBusy || HasPendingRecoveryVerification)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(EncryptedArchiveDestinationPath))
        {
            BrowseEncryptedArchivePath();
            if (string.IsNullOrWhiteSpace(EncryptedArchiveDestinationPath))
            {
                EncryptedArchiveStatusMessage = "Выберите новый путь файла .qnar.";
                return;
            }
        }

        if (string.IsNullOrEmpty(_encryptedArchivePassword))
        {
            EncryptedArchiveStatusMessage = "Задайте пароль архива (он отделён от пароля заметки).";
            return;
        }

        if (!string.Equals(_encryptedArchivePassword, _encryptedArchivePasswordConfirm, StringComparison.Ordinal))
        {
            EncryptedArchiveStatusMessage = "Пароль архива и подтверждение не совпадают.";
            return;
        }

        string destination = EncryptedArchiveDestinationPath.Trim();
        if (!destination.EndsWith(EncryptedArchiveConstants.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            destination += EncryptedArchiveConstants.FileExtension;
            EncryptedArchiveDestinationPath = destination;
        }

        string password = _encryptedArchivePassword;
        int? iterations = TestOverridePbkdf2Iterations;
        string attachmentsDirectory = _attachmentStorage.AttachmentsDirectory;
        string databasePath;
        try
        {
            databasePath = ResolveLiveDatabasePath();
        }
        catch (Exception ex)
        {
            LogEncryptedArchiveFailure("ImportExportViewModel.CreateEncryptedArchive", ex);
            EncryptedArchiveStatusMessage = UserFacingEncryptedArchiveError(ex);
            return;
        }

        CaptureEncryptedArchiveUiContext();
        IsEncryptedArchiveBusy = true;
        EncryptedArchiveStatusMessage = "Создание архива…";

        EncryptedArchiveCreateResult? created = null;
        var bounded = await BoundedOperation.RunAsync(
            ct => Task.Run(() => _encryptedArchiveService.Create(new EncryptedArchiveCreateRequest
            {
                DatabasePath = databasePath,
                AttachmentsDirectory = attachmentsDirectory,
                DestinationArchivePath = destination,
                ArchivePassword = password,
                Pbkdf2Iterations = iterations
            }, ct), ct),
            EncryptedArchiveTimeout,
            CancellationToken.None,
            "ImportExportViewModel.CreateEncryptedArchive",
            BoundedTimeoutBehavior.WaitForOwnedWork,
            _encryptedArchiveOwnership).ConfigureAwait(false);

        ApplyEncryptedArchiveUi(() =>
        {
            try
            {
                if (bounded.Outcome != BoundedOperationOutcome.Success || bounded.Value == null)
                {
                    EncryptedArchiveStatusMessage = bounded.UserMessage;
                    HasPendingRecoveryVerification = false;
                    _pendingVerificationArchivePath = null;
                    ShownRecoveryKey = string.Empty;
                    return;
                }

                created = bounded.Value;

                WipeArchivePasswordBuffers();
                ShownRecoveryKey = created.RecoveryKeyFormatted;
                _pendingVerificationArchivePath = created.ArchivePath;
                _pendingAfterRotation = false;
                EncryptedArchiveDestinationPath = created.ArchivePath;
                HasPendingRecoveryVerification = true;
                EncryptedArchiveStatusMessage =
                    "Архив записан. Сохраните recovery key отдельно, затем введите его ниже для контрольного восстановления. Настройка ещё не завершена.";
            }
            finally
            {
                if (!_encryptedArchiveOwnership.IsCancelling)
                {
                    IsEncryptedArchiveBusy = false;
                }
            }
        });
    }

    public async Task ExecuteVerifyEncryptedArchiveRecoveryAsync()
    {
        if (IsEncryptedArchiveBusy || !HasPendingRecoveryVerification)
        {
            return;
        }

        string? archivePath = _pendingVerificationArchivePath;
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            EncryptedArchiveStatusMessage = EncryptedArchiveSecurityException.GenericUserMessage;
            return;
        }

        if (string.IsNullOrWhiteSpace(_recoveryKeyConfirmation))
        {
            EncryptedArchiveStatusMessage = "Введите recovery key для контрольного восстановления.";
            return;
        }

        string recoveryKey = _recoveryKeyConfirmation;
        CaptureEncryptedArchiveUiContext();
        IsEncryptedArchiveBusy = true;
        EncryptedArchiveStatusMessage = "Контрольное восстановление…";

        var bounded = await BoundedOperation.RunAsync(
            ct => Task.Run(() => _encryptedArchiveService.DryRun(new EncryptedArchiveDryRunRequest
            {
                ArchivePath = archivePath,
                ArchivePassword = null,
                RecoveryKeyFormatted = recoveryKey
            }, ct), ct),
            EncryptedArchiveTimeout,
            CancellationToken.None,
            "ImportExportViewModel.VerifyEncryptedArchiveRecovery",
            BoundedTimeoutBehavior.WaitForOwnedWork,
            _encryptedArchiveOwnership).ConfigureAwait(false);

        ApplyEncryptedArchiveUi(() =>
        {
            try
            {
                if (bounded.Outcome != BoundedOperationOutcome.Success)
                {
                    EncryptedArchiveStatusMessage = bounded.UserMessage;
                    return;
                }

                DateTime verifiedUtc = DateTime.UtcNow;
                RememberSuccessfulEncryptedArchive(archivePath, verifiedUtc);
                ClearEncryptedArchiveSecrets();
                EncryptedArchiveStatusMessage =
                    $"Настройка завершена. Контрольное восстановление recovery key прошло успешно.\nФайл: {LastEncryptedArchivePath}\nВремя (UTC): {LastEncryptedArchiveUtcDisplay}";
            }
            finally
            {
                if (!_encryptedArchiveOwnership.IsCancelling)
                {
                    IsEncryptedArchiveBusy = false;
                }
            }
        });
    }

    public void CancelEncryptedArchiveSetup()
    {
        if (IsEncryptedArchiveBusy)
        {
            return;
        }

        bool wasRotation = _pendingAfterRotation;
        ClearEncryptedArchiveSecrets();
        EncryptedArchiveStatusMessage = wasRotation
            ? "Показ ключа сброшен. Ротация уже записана: старый ключ этот файл не открывает. Нужен сохранённый новый ключ или пароль архива."
            : "Контрольное восстановление отменено. Настройка не завершена.";
    }
    private void ExecuteClose()
    {
        if (IsEncryptedArchiveBusy)
        {
            return;
        }

        _isClosed = true;
        _busyOwnership.SuppressCallbacks();
        _encryptedArchiveOwnership.SuppressCallbacks();
        ClearEncryptedArchiveSecrets();
        CloseAction?.Invoke(ImportedNotesCount > 0);
    }

    public void ClearEncryptedArchiveSecrets()
    {
        WipeArchivePasswordBuffers();
        WipeRotateSecretBuffers();
        _recoveryKeyConfirmation = string.Empty;
        ShownRecoveryKey = string.Empty;
        _pendingVerificationArchivePath = null;
        _pendingAfterRotation = false;
        HasPendingRecoveryVerification = false;
    }

    private void WipeArchivePasswordBuffers()
    {
        _encryptedArchivePassword = string.Empty;
        _encryptedArchivePasswordConfirm = string.Empty;
    }

    private void WipeRotateSecretBuffers()
    {
        _encryptedArchiveRotatePassword = string.Empty;
        _encryptedArchiveRotateCurrentRecovery = string.Empty;
    }

    private void LoadLastSuccessfulEncryptedArchive()
    {
        var settings = _settingsService?.CurrentSettings;
        if (settings == null
            || string.IsNullOrWhiteSpace(settings.LastEncryptedArchivePath)
            || !settings.LastEncryptedArchiveUtc.HasValue)
        {
            return;
        }

        LastEncryptedArchivePath = settings.LastEncryptedArchivePath;
        LastEncryptedArchiveUtc = settings.LastEncryptedArchiveUtc;
        if (string.IsNullOrWhiteSpace(EncryptedArchiveRotatePath))
        {
            EncryptedArchiveRotatePath = settings.LastEncryptedArchivePath;
        }
    }

    private void RememberSuccessfulEncryptedArchive(string path, DateTime utc)
    {
        LastEncryptedArchivePath = path;
        LastEncryptedArchiveUtc = utc;

        if (_settingsService == null)
        {
            return;
        }

        var settings = _settingsService.CurrentSettings;
        settings.LastEncryptedArchivePath = path;
        settings.LastEncryptedArchiveUtc = utc;
        _settingsService.SaveSettings(settings);
    }

    private string ResolveLiveDatabasePath()
    {
        using var db = _contextFactory();
        string? dataSource = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            dataSource = TryParseSqliteDataSource(db.Database.GetConnectionString()) ?? db.DbPath;
        }

        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new EncryptedArchiveValidationException("Файл базы данных не найден.");
        }

        string full = Path.GetFullPath(dataSource);
        if (!File.Exists(full))
        {
            throw new EncryptedArchiveValidationException("Файл базы данных не найден.");
        }

        return full;
    }

    private static string? TryParseSqliteDataSource(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        const string prefix = "Data Source=";
        int start = connectionString.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        string rest = connectionString[(start + prefix.Length)..];
        int end = rest.IndexOf(';');
        string value = (end >= 0 ? rest[..end] : rest).Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string UserFacingEncryptedArchiveError(Exception ex)
        => ex is TimeoutException or OperationCanceledException
            ? UserFacingOperationError.Timeout
            : ex is EncryptedArchiveException
            ? ex.Message
            : "Не удалось создать или проверить архив.";

    private static void LogEncryptedArchiveFailure(string source, Exception ex)
        => ErrorLogService.Write(source, $"{ex.GetType().Name}: {ErrorLogService.Sanitize(ex.Message)}");

    private void CaptureEncryptedArchiveUiContext()
        => _encryptedArchiveUiContext = SynchronizationContext.Current;

    private void ApplyEncryptedArchiveUi(Action action)
    {
        SynchronizationContext? target = _encryptedArchiveUiContext;
        if (target == null || ReferenceEquals(SynchronizationContext.Current, target))
        {
            action();
            return;
        }

        target.Send(_ => action(), null);
    }

    private static string? DefaultEncryptedArchiveSaveDialog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Новый зашифрованный архив QuickNotes",
            Filter = "Зашифрованный архив QuickNotes (*.qnar)|*.qnar",
            DefaultExt = EncryptedArchiveConstants.FileExtension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = false,
            CheckFileExists = false
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? DefaultEncryptedArchiveOpenDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Существующий зашифрованный архив QuickNotes",
            Filter = "Зашифрованный архив QuickNotes (*.qnar)|*.qnar",
            DefaultExt = EncryptedArchiveConstants.FileExtension.TrimStart('.'),
            CheckFileExists = true,
            Multiselect = false
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? DefaultRecoveryKeyOfflineSaveDialog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Новый файл офлайн-копии recovery key",
            Filter = "Текст (*.txt)|*.txt",
            DefaultExt = "txt",
            FileName = RecoveryKeyOfflineCopy.DefaultFileName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            OverwritePrompt = false,
            CheckFileExists = false
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static bool DefaultPrintRecoveryKeyDocument(string document)
    {
        var dialog = new System.Windows.Controls.PrintDialog();
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var flow = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(document)))
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            PagePadding = new Thickness(48)
        };
        dialog.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)flow).DocumentPaginator, FileTitleForPrint());
        return true;
    }

    private static string FileTitleForPrint() => RecoveryKeyOfflineCopy.FileTitle;

    private bool ConfirmEncryptedArchive(string message, string title)
    {
        Func<string, string, bool>? confirm = EncryptedArchiveConfirmProvider ?? ConfirmProvider;
        return confirm?.Invoke(message, title) == true;
    }

    private void RaiseEncryptedArchiveCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanEditEncryptedArchiveCreateInputs));
        OnPropertyChanged(nameof(CanEditEncryptedArchiveRecoveryInputs));
        OnPropertyChanged(nameof(CanEditEncryptedArchiveRotateInputs));
        OnPropertyChanged(nameof(CanCloseImportExportWindow));
        (BrowseEncryptedArchivePathCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CreateEncryptedArchiveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (VerifyEncryptedArchiveRecoveryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelEncryptedArchiveSetupCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveRecoveryKeyOfflineCopyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PrintRecoveryKeyCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseEncryptedArchiveRotatePathCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RotateEncryptedArchiveRecoveryCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CloseCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
