using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.App.Services;

public static class SyncConflictUiSmokeRunner
{
    public const string LocalBody = "Локальный Markdown: план **встречи** и список:\n- пункт A";
    public const string RemoteBody = "Облачный Markdown: правки с другого ПК.\n- пункт B";
    public const string MergeBody = "Итоговый Markdown объединения:\n- пункт A\n- пункт B";
    public const string SecretPlaintext = "СЕКРЕТНЫЙ_PLAINTEXT_НЕ_ДОЛЖЕН_ПОПАСТЬ_НА_ЭКРАН";

    public static void Attach(MainWindow window, string? explicitOutputDir)
    {
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(80);
                string outputDir = ResolveOutputDir(explicitOutputDir);
                Run(outputDir);
                Console.WriteLine("QN_SYNC_CONFLICT_SMOKE_SUCCESS");
                System.Windows.Application.Current.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QN_SYNC_CONFLICT_SMOKE_ERROR:{ex}");
                System.Windows.Application.Current.Shutdown(1);
            }
        };
    }

    public static string ResolveOutputDir(string? explicitOutputDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitOutputDir))
        {
            return Path.GetFullPath(explicitOutputDir);
        }

        string baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "artifacts", "sync-conflict-acceptance"));
    }

    public static void Run(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var device = new FixedDeviceIdProvider(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        SeedConflicts(device.GetDeviceId());

        var service = new SyncConflictService(() => new QuickNotesDbContext(), device, new NoteHistoryService(), new LocalMutationCoordinator());
        var vm = new SyncConflictsViewModel(service, loadOnStart: false);
        vm.LoadConflictsAsync().GetAwaiter().GetResult();
        WaitForDetail(vm);

        var window = new SyncConflictsWindow(vm);
        window.Show();
        window.UpdateLayout();

        CaptureScene(window, vm, outputDir, "compare", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "compare", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "compare", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "compare", 640, 560, AppTheme.Dark);

        CaptureScene(window, vm, outputDir, "merge", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "merge", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "merge", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "merge", 640, 560, AppTheme.Dark);

        CaptureScene(window, vm, outputDir, "protected", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "protected", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "protected", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "protected", 640, 560, AppTheme.Dark);

        CaptureScene(window, vm, outputDir, "success", 780, 720, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "success", 780, 720, AppTheme.Dark);
        CaptureScene(window, vm, outputDir, "success", 640, 560, AppTheme.Light);
        CaptureScene(window, vm, outputDir, "success", 640, 560, AppTheme.Dark);

        window.Close();
    }

    private static void SeedConflicts(Guid localDeviceId)
    {
        using var db = new QuickNotesDbContext();
        DbInitializer.Initialize(db);

        var open = new Note
        {
            Title = "Локальный заголовок конфликта",
            Text = LocalBody,
            UpdatedAt = DateTime.Now.AddMinutes(-40)
        };
        var locked = new Note
        {
            Title = string.Empty,
            Text = string.Empty,
            IsProtected = true,
            ProtectedCiphertextBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("cipher-not-for-ui")),
            UpdatedAt = DateTime.Now.AddMinutes(-20)
        };
        db.Notes.AddRange(open, locked);
        db.SaveChanges();

        var openState = new SyncEntityState
        {
            SyncId = open.SyncId,
            EntityType = "Note",
            RevisionId = Guid.NewGuid(),
            DeviceId = localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-40),
            ContentHash = "open"
        };
        var lockedState = new SyncEntityState
        {
            SyncId = locked.SyncId,
            EntityType = "Note",
            RevisionId = Guid.NewGuid(),
            DeviceId = localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            ContentHash = "locked"
        };
        db.SyncEntityStates.AddRange(openState, lockedState);

        db.SyncConflicts.Add(new SyncConflictRecord
        {
            SyncId = open.SyncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Reason = "Параллельные правки на двух устройствах",
            LocalRevisionId = openState.RevisionId,
            RemoteRevisionId = Guid.NewGuid(),
            LocalDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = open.SyncId,
                Title = open.Title,
                Text = open.Text,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-40),
                DeviceId = localDeviceId
            }),
            RemoteDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = open.SyncId,
                Title = "Облачный заголовок конфликта",
                Text = RemoteBody,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                DeviceId = Guid.Parse("11111111-2222-3333-4444-555555555555")
            })
        });

        db.SyncConflicts.Add(new SyncConflictRecord
        {
            SyncId = locked.SyncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            SourceDeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555"),
            Reason = "Конфликт защищённой заметки",
            LocalRevisionId = lockedState.RevisionId,
            RemoteRevisionId = Guid.NewGuid(),
            LocalDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = locked.SyncId,
                IsProtected = true,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                DeviceId = localDeviceId
            }),
            RemoteDataJson = JsonSerializer.Serialize(new SyncNoteDto
            {
                SyncId = locked.SyncId,
                IsProtected = true,
                Text = SecretPlaintext,
                ProtectedCiphertextBase64 = "AAAA",
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-8),
                DeviceId = Guid.Parse("99999999-8888-7777-6666-555555555555")
            })
        });
        db.SaveChanges();
    }

    private static void CaptureScene(SyncConflictsWindow window, SyncConflictsViewModel vm, string outputDir, string scene, int width, int height, AppTheme theme)
    {
        ThemeService.ApplyTheme(theme);
        PrepareScene(vm, scene);
        window.UpdateLayout();

        string size = width >= 720 ? "wide" : "narrow";
        string themeName = theme == AppTheme.Dark ? "dark" : "light";
        string path = Path.Combine(outputDir, $"{scene}-{themeName}-{size}.png");
        ThreePaneUiSmokeRunner.RenderAndSaveScreenshot(window, path, width, height, w => AssertScene(w, vm, scene));
        ThreePaneUiSmokeRunner.ValidateScreenshot(path, width, height);

        byte[] png = File.ReadAllBytes(path);
        string pngText = System.Text.Encoding.UTF8.GetString(png);
        if (pngText.Contains(SecretPlaintext, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Protected plaintext leaked into screenshot bytes.");
        }
    }

    private static void PrepareScene(SyncConflictsViewModel vm, string scene)
    {
        if (scene == "success")
        {
            while (vm.Conflicts.Count > 0 && vm.SelectedConflict != null)
            {
                vm.ResolveKeepLocalAsync().GetAwaiter().GetResult();
            }

            if (!vm.HasCompletedResolution)
            {
                throw new InvalidOperationException(vm.StatusMessage ?? "success summary missing");
            }

            vm.StatusMessage = vm.SuccessSummary;
            return;
        }

        if (vm.Conflicts.Count == 0)
        {
            throw new InvalidOperationException("Smoke conflicts were not loaded.");
        }

        if (scene == "protected")
        {
            vm.SelectedConflict = vm.Conflicts.Count > 1 ? vm.Conflicts[1] : vm.Conflicts[0];
        }
        else
        {
            vm.ClearMergeChoices();
            vm.SelectedConflict = vm.Conflicts[0];
        }

        WaitForDetail(vm);

        if (scene == "merge")
        {
            vm.UseLocalTitle = true;
            vm.UseRemoteTags = true;
            vm.MergedText = MergeBody;
            vm.StatusMessage = "Черновик объединения готов. Колонки версий только для чтения.";
        }
        else if (scene == "compare")
        {
            vm.ClearMergeChoices();
            vm.StatusMessage = "Сравнение: локальная и облачная колонки, устройство, время и причина.";
        }
        else
        {
            vm.StatusMessage = "Защищённая версия: только безопасные метаданные, без plaintext.";
        }
    }

    private static void AssertScene(Window window, SyncConflictsViewModel vm, string scene)
    {
        window.UpdateLayout();
        if (scene == "success")
        {
            var summary = Named<Border>(window, "ConflictSuccessSummaryPanel");
            summary.BringIntoView();
            window.UpdateLayout();
            if (!vm.HasCompletedResolution || string.IsNullOrWhiteSpace(vm.SuccessSummary))
            {
                throw new InvalidOperationException("Success scene is not a post-resolution state.");
            }

            NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, summary, "ConflictSuccessSummaryPanel");
            return;
        }

        var localBody = Named<TextBox>(window, "LocalBodyBox");
        var remoteBody = Named<TextBox>(window, "RemoteBodyBox");
        var reason = Named<TextBlock>(window, "ConflictReasonText");
        var keepLocal = Named<Button>(window, "KeepLocalButton");
        keepLocal.BringIntoView();
        reason.BringIntoView();
        window.UpdateLayout();
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, keepLocal, "KeepLocalButton");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, reason, "ConflictReasonText");

        if (scene == "compare")
        {
            if (!localBody.Text.Contains("пункт A", StringComparison.Ordinal)
                || !remoteBody.Text.Contains("пункт B", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(reason.Text)
                || vm.HasDefaultDestructiveChoice
                || vm.MergeCommand.CanExecute(null))
            {
                throw new InvalidOperationException("Compare scene must show both bodies, reason, no default merge commit.");
            }
        }

        if (scene == "merge")
        {
            var editor = Named<TextBox>(window, "MergeEditorBox");
            editor.BringIntoView();
            window.UpdateLayout();
            if (!editor.Text.Contains("Итоговый Markdown", StringComparison.Ordinal)
                || !vm.CanCommitMerge
                || localBody.IsReadOnly != true
                || remoteBody.IsReadOnly != true)
            {
                throw new InvalidOperationException("Merge scene must edit only the result box.");
            }

            NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, editor, "MergeEditorBox");
            NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, Named<RadioButton>(window, "MergeTitleLocalRadio"), "MergeTitleLocalRadio");
        }

        if (scene == "protected")
        {
            string combined = localBody.Text + remoteBody.Text + (vm.SelectedConflict?.Detail?.LocalText ?? "") + (vm.SelectedConflict?.Detail?.RemoteText ?? "");
            if (combined.Contains(SecretPlaintext, StringComparison.Ordinal)
                || vm.SelectedConflict?.IsProtectedConflict != true)
            {
                throw new InvalidOperationException("Protected scene leaked plaintext or did not select a protected conflict.");
            }
        }
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var found = FindNamed<T>(root, name);
        if (found == null)
        {
            throw new InvalidOperationException($"Missing element {name}.");
        }

        return found;
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is FrameworkElement fe && fe.Name == name && root is T match)
        {
            return match;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindNamed<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found != null)
            {
                return found;
            }
        }

        if (root is ContentControl cc && cc.Content is DependencyObject content)
        {
            return FindNamed<T>(content, name);
        }

        return null;
    }

    private static void WaitForDetail(SyncConflictsViewModel vm)
    {
        vm.PendingDetailLoad.GetAwaiter().GetResult();
        for (int i = 0; i < 80; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (vm.SelectedConflict?.Detail != null || vm.SelectedConflict == null)
            {
                return;
            }

            System.Threading.Thread.Sleep(25);
        }

        throw new InvalidOperationException("Conflict detail did not load.");
    }
}

