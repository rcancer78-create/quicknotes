using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Services;

namespace QuickNotes.App.Helpers;

public static class Win32Helper
{
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_C = 0x43;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool IsLikelyElevatedWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0)
            {
                return false;
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            if (!OpenProcessToken(process.Handle, 0x0008, out var token))
            {
                return Marshal.GetLastWin32Error() == 5;
            }

            try
            {
                GetTokenInformation(token, 20, IntPtr.Zero, 0, out int length);
                if (length <= 0)
                {
                    return false;
                }

                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, 20, buffer, length, out _))
                    {
                        return false;
                    }

                    int elevation = Marshal.ReadInt32(buffer);
                    return elevation != 0 && !IsCurrentProcessElevated();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static string? GetProcessName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return null;
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("GetProcessName", ex);
            return null;
        }
    }

    public static string? GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;
        try
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0) return null;
            var sb = new System.Text.StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            return string.IsNullOrEmpty(title) ? null : title;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("GetWindowTitle", ex);
            return null;
        }
    }

    /// <summary>
    /// Test hook replacing UI Automation probing. Production code leaves this null.
    /// </summary>
    internal static Func<IntPtr, string?>? BrowserUrlCoreOverrideForTests { get; set; }

    private static readonly object BrowserUrlProbeSync = new();
    private static Task<string?>? BrowserUrlInFlight;
    private static int BrowserUrlEpoch;
    private static long BrowserUrlNextAllowedStartTimestamp;

    internal static void ResetBrowserUrlProbeStateForTests()
    {
        lock (BrowserUrlProbeSync)
        {
            BrowserUrlInFlight = null;
            BrowserUrlEpoch++;
            BrowserUrlNextAllowedStartTimestamp = 0;
        }
    }

    public static async Task<string?> TryGetBrowserUrlAsync(IntPtr hWnd, CancellationToken cancellationToken = default)
    {
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        int epoch;
        Task<string?> work;
        lock (BrowserUrlProbeSync)
        {
            long now = Environment.TickCount64;
            if (BrowserUrlInFlight != null && !BrowserUrlInFlight.IsCompleted)
            {
                return null;
            }

            if (now < BrowserUrlNextAllowedStartTimestamp)
            {
                return null;
            }

            epoch = ++BrowserUrlEpoch;
            BrowserUrlNextAllowedStartTimestamp = now + (long)BoundedOperation.BrowserUrlCaptureTimeout.TotalMilliseconds;
            try
            {
                work = Task.Run(() => ProbeBrowserUrl(hWnd));
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("BrowserUrl", ex);
                return null;
            }

            BrowserUrlInFlight = work;
            _ = work.ContinueWith(
                completed =>
                {
                    lock (BrowserUrlProbeSync)
                    {
                        if (ReferenceEquals(BrowserUrlInFlight, completed))
                        {
                            BrowserUrlInFlight = null;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        try
        {
            string? url = await work
                .WaitAsync(BoundedOperation.BrowserUrlCaptureTimeout, cancellationToken)
                .ConfigureAwait(false);
            lock (BrowserUrlProbeSync)
            {
                if (epoch != BrowserUrlEpoch)
                {
                    return null;
                }
            }

            return url;
        }
        catch (TimeoutException)
        {
            BoundedOperation.ObserveOrphan(work, "BrowserUrl");
            return null;
        }
        catch (OperationCanceledException)
        {
            BoundedOperation.ObserveOrphan(work, "BrowserUrl");
            return null;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("BrowserUrl", ex);
            return null;
        }
    }

    private static string? ProbeBrowserUrl(IntPtr hWnd)
    {
        if (BrowserUrlCoreOverrideForTests != null)
        {
            return BrowserUrlCoreOverrideForTests(hWnd);
        }

        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hWnd);
            if (root == null)
            {
                return null;
            }

            try
            {
                var ffBar = root.FindFirst(
                    System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "urlbar-input"));
                if (ffBar != null && ffBar.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var p) &&
                    p is System.Windows.Automation.ValuePattern vp)
                {
                    var sanitized = Models.NoteSourceContext.SanitizeUrl(vp.Current.Value);
                    if (sanitized != null)
                    {
                        return sanitized;
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("BrowserUrl.Firefox", ex);
            }

            var condition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Edit);
            var edits = root.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
            foreach (System.Windows.Automation.AutomationElement edit in edits)
            {
                try
                {
                    if (edit.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var pattern) &&
                        pattern is System.Windows.Automation.ValuePattern vp)
                    {
                        var sanitized = Models.NoteSourceContext.SanitizeUrl(vp.Current.Value);
                        if (sanitized != null)
                        {
                            return sanitized;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("BrowserUrl.Edit", ex);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("BrowserUrl.Tree", ex);
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Sends Ctrl+C keystroke via SendInput
    /// </summary>
    public static void SendCtrlC()
    {
        var inputs = new INPUT[4];

        // 1. Ctrl Down
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 }
            }
        };

        // 2. C Down
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT { wVk = VK_C, dwFlags = 0 }
            }
        };

        // 3. C Up
        inputs[2] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT { wVk = VK_C, dwFlags = KEYEVENTF_KEYUP }
            }
        };

        // 4. Ctrl Up
        inputs[3] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP }
            }
        };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Не удалось отправить Ctrl+C активному приложению. Проверьте его уровень прав.");
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();
}
