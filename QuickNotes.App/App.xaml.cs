using System;
using System.IO;
using System.Windows;
using QuickNotes.App.Composition;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App;

public partial class App : System.Windows.Application
{
    private readonly bool _resourcesOnly;

    public App() : this(resourcesOnly: false)
    {
    }

    // The testhost needs the real App.xaml resources, but owns its own windows
    // and services. WPF queues OnStartup even when only Dispatcher.Run is used.
    internal App(bool resourcesOnly)
    {
        _resourcesOnly = resourcesOnly;
        InitializeComponent();
    }

    private SingleInstanceService? _singleInstanceService;
    private ApplicationCompositionRoot? _composition;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (_resourcesOnly)
        {
            return;
        }

        var startupStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var isolatedOptions = IsolatedProfileAppCli.Parse(e.Args);
        var phases = isolatedOptions.PerfStartup
            ? IsolatedProfileAppCli.BeginStartupPhases(startupStopwatch)
            : null;
        if (isolatedOptions.IsIsolated && !string.IsNullOrEmpty(isolatedOptions.ProfileDirectory))
        {
            QuickNotesDbContext.ProfileDirectoryOverride = isolatedOptions.ProfileDirectory;
        }
        else if (EncryptedArchiveRestoreCli.Parse(e.Args).IsRestoreCommand
                 && EncryptedArchiveRestoreCli.TryRun(e.Args, out int restoreExitCode))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(restoreExitCode);
            return;
        }

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLogService.Write("Dispatcher", args.Exception);
            args.Handled = true;
            if (isolatedOptions.IsIsolated)
            {
                Console.Error.WriteLine($"[IsolatedProfileError] Dispatcher: {args.Exception.GetType().Name}: {args.Exception.Message}");
                Shutdown(1);
                return;
            }
            System.Windows.MessageBox.Show(
                $"Произошла ошибка. Подробности записаны в журнал без текста заметок.\n\n{args.Exception.GetType().Name}: {ErrorLogService.Sanitize(args.Exception.Message)}",
                "QuickNotes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                ErrorLogService.Write("AppDomain", ex);
                if (isolatedOptions.IsIsolated)
                {
                    Console.Error.WriteLine($"[IsolatedProfileError] AppDomain: {ex.GetType().Name}: {ex.Message}");
                }
            }
        };

        // 1. Single-Instance Check (must run before hotkeys, tray, or UI)
        try
        {
            string instanceId = isolatedOptions.IsIsolated
                ? "Isolated_" + Math.Abs(isolatedOptions.ProfileDirectory!.GetHashCode()).ToString("X")
                : Environment.UserName;
            _singleInstanceService = new SingleInstanceService(instanceId);
            if (!_singleInstanceService.IsPrimary)
            {
                // Secondary instance: signal running primary instance and exit immediately
                _singleInstanceService.SignalExistingInstance();
                _singleInstanceService.Dispose();
                Shutdown();
                return;
            }

            // Primary instance: listen for activation signals from future instances
            _singleInstanceService.StartListening(() =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _mainWindow?.BringWindowToFront();
                });
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Не удалось проверить, запущен ли QuickNotes:\n{ex.Message}",
                "Критическая ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        phases?.Mark("single_instance");

        // 2. Profile directory must exist before integrity/recovery/SQLite access.
        var preparedProfile = StartupProfilePreparation.Prepare(isolatedOptions);
        if (!preparedProfile.Succeeded)
        {
            ErrorLogService.Write("Startup.Profile", preparedProfile.UserMessage);
            if (isolatedOptions.IsIsolated)
            {
                Console.Error.WriteLine($"[IsolatedProfileError] Startup.Profile: {preparedProfile.UserMessage}");
                Shutdown(1);
                return;
            }

            System.Windows.MessageBox.Show(
                preparedProfile.UserMessage,
                "Критическая ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 3. Integrity check & Database Recovery Flow (pre-root, same profile-scoped backup paths)
        string profileDirectory = preparedProfile.ProfileDirectory;
        var backupService = ApplicationCompositionRoot.CreateProfileBackupService(profileDirectory);
        var recoveryService = new DatabaseRecoveryService(backupService.DbPath, backupService);

        bool databaseFileExistedAndNonEmpty = File.Exists(backupService.DbPath)
            && new FileInfo(backupService.DbPath).Length > 0;
        bool probeReportedCorrupt = recoveryService.IsDatabaseCorrupt(out var corruptReason);
        if (probeReportedCorrupt)
        {
            ErrorLogService.Write("Database.Recovery", $"Corrupted database detected on startup: {corruptReason}");
            var healthyBackup = recoveryService.FindLatestHealthyBackup(out var backupDiag);

            if (healthyBackup == null)
            {
                string preservedCorrupted = recoveryService.PreserveCorruptedDatabase();
                System.Windows.MessageBox.Show(
                    $"Файл базы данных повреждён:\n{ErrorLogService.Sanitize(corruptReason)}\n\n" +
                    $"Резервные копии недоступны ({backupDiag}).\n" +
                    (string.IsNullOrEmpty(preservedCorrupted) ? "" : $"Повреждённый файл сохранён как:\n{System.IO.Path.GetFileName(preservedCorrupted)}\n\n") +
                    "Приложение не может быть запущено.",
                    "Повреждение базы данных",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
                return;
            }

            var confirm = System.Windows.MessageBox.Show(
                $"Обнаружено повреждение базы данных QuickNotes.\n\n" +
                $"Причина: {ErrorLogService.Sanitize(corruptReason)}\n\n" +
                $"Найдена резервная копия:\n{healthyBackup.Name} (от {healthyBackup.LastWriteTime:dd.MM.yyyy HH:mm:ss})\n\n" +
                $"Перед восстановлением текущий файл базы данных будет сохранён под отдельным именем.\n\n" +
                $"Восстановить базу данных из резервной копии?",
                "Восстановление базы данных",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                var recResult = recoveryService.PerformRecovery(healthyBackup.FullName);
                if (recResult.Success)
                {
                    System.Windows.MessageBox.Show(
                        $"База данных успешно восстановлена из резервной копии:\n{healthyBackup.Name}\n\n" +
                        $"Повреждённый исходный файл сохранён как:\n{System.IO.Path.GetFileName(recResult.PreservedCorruptPath)}",
                        "Восстановление завершено",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        $"Не удалось восстановить базу данных:\n{recResult.ErrorMessage}\n\n" +
                        "Исходный файл базы данных не был изменён.",
                        "Ошибка восстановления",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "Восстановление отменено пользователем. Приложение будет закрыто.",
                    "Отмена запуска",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        phases?.Mark("db_integrity");

        try
        {
            using var initContext = new QuickNotesDbContext(backupService.DbPath);
            bool reuseRecoveryIntegrity = DbInitializer.CanReusePriorIntegrityCheck(
                databaseFileExistedAndNonEmpty,
                probeReportedCorrupt);
            DbInitializer.Initialize(
                initContext,
                () => backupService.CreateBackup(),
                integrityAlreadyVerified: reuseRecoveryIntegrity);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Database.Init", ex);
            System.Windows.MessageBox.Show(
                $"Ошибка инициализации базы данных:\n{ex.Message}",
                "Критическая ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        phases?.Mark("db_initialize");

        // 4. Initialize composition graph
        _composition = ApplicationCompositionRoot.CreateForStartup(isolatedOptions, backupService);
        var settingsService = _composition.Core.Settings;
        LegacyUnsupportedIndexCleanup.TryDeleteKnownIndexFiles(_composition.ProfileDirectory);
        ThemeService.ApplyTheme(settingsService.CurrentSettings.Theme);
        ThemeService.InitializeListener(() =>
        {
            if (settingsService.CurrentSettings.Theme == Models.AppTheme.System)
            {
                ThemeService.ApplyTheme(Models.AppTheme.System);
            }
        });
        if (settingsService.LoadWarning != null)
            System.Windows.MessageBox.Show(settingsService.LoadWarning, "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (_composition.Core.TagRules.LoadWarning != null)
            System.Windows.MessageBox.Show(_composition.Core.TagRules.LoadWarning, "Правила автотегов", MessageBoxButton.OK, MessageBoxImage.Warning);

        var hotkeyService = _composition.Capture.Hotkeys;
        if (isolatedOptions.IsIsolated)
        {
            // Isolated CLI never registers live hotkeys.
        }
        else
        {
            bool registered = hotkeyService.Register(settingsService.CurrentSettings, out var hotkeyError);
            if (!registered && !string.IsNullOrEmpty(hotkeyError))
            {
                System.Windows.MessageBox.Show(
                    hotkeyError,
                    "Предупреждение о горячей клавише",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        phases?.Mark("composition");

        var mainVm = _composition.CreateMainViewModel();
        var trayIconService = _composition.Ui.Tray;

        _mainWindow = new MainWindow(mainVm);
        mainVm.RequestCloseMainWindowDecision ??= () => CloseToTrayPrompt.Show(_mainWindow);
        MainWindow = _mainWindow;

        if (isolatedOptions.PerfStartup)
        {
            IsolatedProfileAppCli.AttachStartupBenchmark(_mainWindow, startupStopwatch, phases);
        }
        else if (isolatedOptions.PerfScroll)
        {
            IsolatedProfileAppCli.AttachScrollBenchmark(_mainWindow);
        }
        else if (isolatedOptions.ThreePaneSmoke)
        {
            IsolatedProfileAppCli.AttachThreePaneSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.ImportMigrationSmoke)
        {
            IsolatedProfileAppCli.AttachImportMigrationSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.NoteAssemblySmoke)
        {
            IsolatedProfileAppCli.AttachNoteAssemblySmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.TasksIndexSmoke)
        {
            IsolatedProfileAppCli.AttachTasksIndexSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.RemindersSmoke)
        {
            IsolatedProfileAppCli.AttachRemindersSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.SyncConflictSmoke)
        {
            IsolatedProfileAppCli.AttachSyncConflictSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxPackageAbSmoke)
        {
            IsolatedProfileAppCli.AttachUxPackageAbSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxC08Smoke)
        {
            IsolatedProfileAppCli.AttachUxC08Smoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxC11Smoke)
        {
            IsolatedProfileAppCli.AttachUxC11Smoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxC12Smoke)
        {
            IsolatedProfileAppCli.AttachUxC12Smoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxC13Smoke)
        {
            IsolatedProfileAppCli.AttachUxC13Smoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxC14Smoke)
        {
            IsolatedProfileAppCli.AttachUxC14Smoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.UxPackageDLayoutSmoke)
        {
            IsolatedProfileAppCli.AttachUxPackageDLayoutSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.WorkspaceBoardSmoke)
        {
            IsolatedProfileAppCli.AttachWorkspaceBoardSmoke(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }
        else if (isolatedOptions.ManualAcceptancePrep)
        {
            IsolatedProfileAppCli.AttachManualAcceptancePrep(_mainWindow, isolatedOptions.ArtifactsOutputDir);
        }

        if (!isolatedOptions.IsIsolated)
        {
            mainVm.StartSyncScheduler();
        }

        mainVm.StartTaskReminders();

        phases?.Mark("main_viewmodel");

        trayIconService.ExitRequested += () =>
        {
            _mainWindow.ExitApplication();
        };

        if (isolatedOptions.PerfStartup || isolatedOptions.PerfScroll || isolatedOptions.ThreePaneSmoke || isolatedOptions.ImportMigrationSmoke || isolatedOptions.NoteAssemblySmoke || isolatedOptions.TasksIndexSmoke || isolatedOptions.RemindersSmoke || isolatedOptions.SyncConflictSmoke || isolatedOptions.UxPackageAbSmoke || isolatedOptions.UxC08Smoke || isolatedOptions.UxC11Smoke || isolatedOptions.UxC12Smoke || isolatedOptions.UxC13Smoke || isolatedOptions.UxC14Smoke || isolatedOptions.UxPackageDLayoutSmoke || isolatedOptions.WorkspaceBoardSmoke || isolatedOptions.ManualAcceptancePrep || !settingsService.CurrentSettings.StartMinimizedToTray)
        {
            _mainWindow.Show();
        }

        phases?.Mark("window_shown");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _composition?.Security.Protection.WipeAllSessions();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("App.OnExit.WipeSessions", ex);
        }

        try
        {
            if (_composition != null)
            {
                _composition.TeardownThenDispose(
                    _mainWindow?.DataContext as MainViewModel,
                    BoundedOperation.ShutdownWaitTimeout);
            }
            else
            {
                (_mainWindow?.DataContext as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("App.OnExit.Shutdown", ex);
        }

        _singleInstanceService?.Dispose();
        base.OnExit(e);
    }
}
