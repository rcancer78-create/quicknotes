using System.Runtime.InteropServices;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using Clipboard = System.Windows.Clipboard;

namespace QuickNotes.App.Services;

public class ClipboardCaptureService
{
    private bool _capturing;

    public async Task<CapturedNoteContext?> CaptureContextAsync()
    {
        var attempt = await CaptureWithDiagnosticsAsync();
        return attempt.Context;
    }

    public async Task<string?> CaptureSelectedTextAsync()
    {
        var context = await CaptureContextAsync();
        return context?.Text;
    }

    public async Task<ClipboardCaptureAttempt> CaptureWithDiagnosticsAsync()
    {
        if (_capturing)
        {
            return new ClipboardCaptureAttempt { FailureReason = "Захват уже выполняется. Подождите секунду и повторите." };
        }

        _capturing = true;
        try
        {
            var foreground = Win32Helper.GetForegroundWindow();
            for (int i = 0; ModifiersPressed(); i++)
            {
                if (i >= 100)
                {
                    return new ClipboardCaptureAttempt
                    {
                        FailureReason = "Не удалось дождаться отпускания клавиш-модификаторов. Отпустите Ctrl, Shift, Alt и Win и повторите."
                    };
                }
                await Task.Delay(20);
            }

            if (foreground != Win32Helper.GetForegroundWindow())
            {
                return new ClipboardCaptureAttempt
                {
                    FailureReason = "Активное окно сменилось до копирования. Повторите захват, не переключая окна."
                };
            }

            if (Win32Helper.IsLikelyElevatedWindow(foreground))
            {
                return new ClipboardCaptureAttempt
                {
                    FailureReason = "Окно работает с повышенными правами. QuickNotes не может скопировать из него выделенный текст. Запустите программу от имени администратора или скопируйте текст вручную."
                };
            }

            string? processName = Win32Helper.GetProcessName(foreground);
            string? windowTitle = Win32Helper.GetWindowTitle(foreground);
            string? url = await Win32Helper.TryGetBrowserUrlAsync(foreground);
            DateTime capturedAt = DateTime.Now;

            System.Windows.IDataObject? original;
            try
            {
                original = await RetryAsync(() =>
                {
                    var source = Clipboard.GetDataObject();
                    var copy = new System.Windows.DataObject();
                    if (source != null)
                    {
                        foreach (var format in source.GetFormats(false))
                        {
                            var value = source.GetData(format, false);
                            if (value != null) copy.SetData(format, value, false);
                        }
                    }
                    return copy;
                });
            }
            catch (ExternalException)
            {
                return new ClipboardCaptureAttempt
                {
                    FailureReason = "Буфер обмена занят другим приложением. Закройте диалоги вставки и повторите захват."
                };
            }

            try
            {
                await RetryAsync(() => { Clipboard.Clear(); return true; });
            }
            catch (ExternalException)
            {
                return new ClipboardCaptureAttempt
                {
                    FailureReason = "Не удалось освободить буфер обмена — он занят. Повторите через несколько секунд."
                };
            }

            uint ownedSequence = Win32Helper.GetClipboardSequenceNumber();
            try
            {
                if (foreground != Win32Helper.GetForegroundWindow())
                {
                    return new ClipboardCaptureAttempt
                    {
                        FailureReason = "Активное окно сменилось в момент копирования. Повторите захват."
                    };
                }

                Win32Helper.SendCtrlC();
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(40);
                    uint sequence = Win32Helper.GetClipboardSequenceNumber();
                    if (sequence == ownedSequence) continue;
                    ownedSequence = sequence;

                    string? text;
                    try
                    {
                        text = await RetryAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                    }
                    catch (ExternalException)
                    {
                        return new ClipboardCaptureAttempt
                        {
                            FailureReason = "Буфер обмена занят во время чтения скопированного текста. Повторите захват."
                        };
                    }

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return new ClipboardCaptureAttempt
                        {
                            FailureReason = "В активном окне нет выделенного текста (или скопировалось пустое содержимое)."
                        };
                    }

                    return new ClipboardCaptureAttempt
                    {
                        Context = new CapturedNoteContext
                        {
                            Text = text,
                            ProcessName = processName,
                            WindowTitle = windowTitle,
                            Url = url,
                            CapturedAt = capturedAt
                        }
                    };
                }

                return new ClipboardCaptureAttempt
                {
                    FailureReason = Win32Helper.IsLikelyElevatedWindow(foreground)
                        ? "Окно с повышенными правами не отдало выделенный текст."
                        : "Копирование не успело завершиться. Выделите текст и повторите захват."
                };
            }
            finally
            {
                try
                {
                    await RetryAsync(() =>
                    {
                        if (Win32Helper.GetClipboardSequenceNumber() == ownedSequence)
                        {
                            if (original.GetFormats(false).Length == 0) Clipboard.Clear();
                            else Clipboard.SetDataObject(original, true);
                        }
                        return true;
                    });
                }
                catch (ExternalException)
                {
                    // Restore best-effort; the capture outcome is already decided.
                }
            }
        }
        finally
        {
            _capturing = false;
        }
    }

    private static bool ModifiersPressed() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (Win32Helper.GetAsyncKeyState(k) & 0x8000) != 0);

    private static async Task<T> RetryAsync<T>(Func<T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (ExternalException) when (attempt < 5)
            {
                await Task.Delay(40);
            }
        }
    }
}
