using System;
using System.Windows.Input;
using System.Windows.Interop;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class GlobalHotkeyService : IDisposable
{
    public delegate bool RegisterHotKeyDelegate(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    public delegate bool UnregisterHotKeyDelegate(IntPtr hWnd, int id);

    private readonly RegisterHotKeyDelegate _registerHotKey;
    private readonly UnregisterHotKeyDelegate _unregisterHotKey;
    private readonly bool _useRealHwnd;
    private readonly IntPtr _fakeHwnd;

    private HwndSource? _hwndSource;
    private bool _disposed;

    private int _editorId = 9001;
    private uint _editorModifiers, _editorVk;
    private bool _editorRegistered;

    private int _instantSaveId = 9003;
    private uint _instantSaveModifiers, _instantSaveVk;
    private bool _instantSaveRegistered;

    private int _ocrId = 9005;
    private uint _ocrModifiers, _ocrVk;
    private bool _ocrRegistered;
    private bool _ocrSuspended;
    private bool _ocrConfigured;

    public event Action? HotkeyPressed;
    public event Action? EditorHotkeyPressed;
    public event Action? InstantSaveHotkeyPressed;
    public event Action? OcrHotkeyPressed;

    public bool IsEditorRegistered => _editorRegistered;
    public bool IsInstantSaveRegistered => _instantSaveRegistered;
    public bool IsOcrRegistered => _ocrRegistered;

    public GlobalHotkeyService() : this(Win32Helper.RegisterHotKey, Win32Helper.UnregisterHotKey)
    {
    }

    public GlobalHotkeyService(RegisterHotKeyDelegate registerFunc, UnregisterHotKeyDelegate unregisterFunc, IntPtr? testHwnd = null)
    {
        _registerHotKey = registerFunc;
        _unregisterHotKey = unregisterFunc;
        if (testHwnd.HasValue)
        {
            _useRealHwnd = false;
            _fakeHwnd = testHwnd.Value;
        }
        else
        {
            _useRealHwnd = true;
            InitializeMessageSink();
        }
    }

    private void InitializeMessageSink()
    {
        var parameters = new HwndSourceParameters("QuickNotesHotkeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = -2000,
            PositionY = -2000,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_editorRegistered && id == _editorId)
            {
                EditorHotkeyPressed?.Invoke();
                HotkeyPressed?.Invoke();
                handled = true;
            }
            else if (_instantSaveRegistered && id == _instantSaveId)
            {
                InstantSaveHotkeyPressed?.Invoke();
                handled = true;
            }
            else if (_ocrRegistered && id == _ocrId)
            {
                OcrHotkeyPressed?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public static bool ValidateSettings(AppSettings settings, out string? errorMessage)
    {
        errorMessage = null;

        // 1. Validate Editor hotkey
        uint editorMod = Win32Helper.MOD_NOREPEAT;
        if (settings.HotkeyCtrl) editorMod |= Win32Helper.MOD_CONTROL;
        if (settings.HotkeyShift) editorMod |= Win32Helper.MOD_SHIFT;
        if (settings.HotkeyAlt) editorMod |= Win32Helper.MOD_ALT;
        if (settings.HotkeyWin) editorMod |= Win32Helper.MOD_WIN;

        if (editorMod == Win32Helper.MOD_NOREPEAT)
        {
            errorMessage = "Выберите хотя бы одну клавишу-модификатор для открытия редактора (Ctrl, Shift, Alt или Win).";
            return false;
        }

        if (!Enum.TryParse<Key>(settings.HotkeyKey, true, out var editorKey) || KeyInterop.VirtualKeyFromKey(editorKey) == 0)
        {
            errorMessage = "Недопустимая клавиша для открытия редактора.";
            return false;
        }

        // 2. Validate Instant Save hotkey
        uint instantMod = Win32Helper.MOD_NOREPEAT;
        if (settings.InstantHotkeyCtrl) instantMod |= Win32Helper.MOD_CONTROL;
        if (settings.InstantHotkeyShift) instantMod |= Win32Helper.MOD_SHIFT;
        if (settings.InstantHotkeyAlt) instantMod |= Win32Helper.MOD_ALT;
        if (settings.InstantHotkeyWin) instantMod |= Win32Helper.MOD_WIN;

        if (instantMod == Win32Helper.MOD_NOREPEAT)
        {
            errorMessage = "Выберите хотя бы одну клавишу-модификатор для мгновенного сохранения (Ctrl, Shift, Alt или Win).";
            return false;
        }

        if (!Enum.TryParse<Key>(settings.InstantHotkeyKey, true, out var instantKey) || KeyInterop.VirtualKeyFromKey(instantKey) == 0)
        {
            errorMessage = "Недопустимая клавиша для мгновенного сохранения.";
            return false;
        }

        // 3. Validate OCR hotkey
        uint ocrMod = Win32Helper.MOD_NOREPEAT;
        if (settings.OcrHotkeyCtrl) ocrMod |= Win32Helper.MOD_CONTROL;
        if (settings.OcrHotkeyShift) ocrMod |= Win32Helper.MOD_SHIFT;
        if (settings.OcrHotkeyAlt) ocrMod |= Win32Helper.MOD_ALT;
        if (settings.OcrHotkeyWin) ocrMod |= Win32Helper.MOD_WIN;

        if (ocrMod == Win32Helper.MOD_NOREPEAT)
        {
            errorMessage = "Выберите хотя бы одну клавишу-модификатор для снимка экрана и OCR (Ctrl, Shift, Alt или Win).";
            return false;
        }

        if (!Enum.TryParse<Key>(settings.OcrHotkeyKey, true, out var ocrKey) || KeyInterop.VirtualKeyFromKey(ocrKey) == 0)
        {
            errorMessage = "Недопустимая клавиша для снимка экрана и OCR.";
            return false;
        }

        // 4. Validate no conflict between any two hotkeys
        int editorVk = KeyInterop.VirtualKeyFromKey(editorKey);
        int instantVk = KeyInterop.VirtualKeyFromKey(instantKey);
        int ocrVk = KeyInterop.VirtualKeyFromKey(ocrKey);

        if (editorMod == instantMod && editorVk == instantVk)
        {
            errorMessage = "Горячие клавиши для открытия редактора и мгновенного сохранения не должны совпадать.";
            return false;
        }

        if (editorMod == ocrMod && editorVk == ocrVk)
        {
            errorMessage = "Горячие клавиши для открытия редактора и снимка экрана (OCR) не должны совпадать.";
            return false;
        }

        if (instantMod == ocrMod && instantVk == ocrVk)
        {
            errorMessage = "Горячие клавиши для мгновенного сохранения и снимка экрана (OCR) не должны совпадать.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Settings UI rejects OCR chords that collide with in-editor Markdown commands.
    /// Startup <see cref="Register"/> must not, so existing profiles that still use
    /// Ctrl+Shift+O keep capture/OCR outside the note body (where OCR is suspended).
    /// </summary>
    public static bool ValidateOcrAgainstEditorReserved(AppSettings settings, out string? errorMessage)
    {
        errorMessage = null;
        if (!ShortcutCatalog.ConflictsWithEditorReserved(settings, out var reservedName))
        {
            return true;
        }

        errorMessage = $"Сочетание снимка экрана (OCR) совпадает с командой редактора ({reservedName}). Выберите другое сочетание.";
        return false;
    }

    public void SetOcrSuspended(bool suspended)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ocrSuspended == suspended)
        {
            return;
        }

        _ocrSuspended = suspended;
        IntPtr handle = _hwndSource?.Handle ?? _fakeHwnd;
        if (handle == IntPtr.Zero || !_ocrConfigured)
        {
            return;
        }

        if (suspended)
        {
            if (_ocrRegistered)
            {
                _unregisterHotKey(handle, _ocrId);
                _ocrRegistered = false;
            }

            return;
        }

        if (!_ocrRegistered)
        {
            int nextOcrId = _ocrId == 9005 ? 9006 : 9005;
            if (_registerHotKey(handle, nextOcrId, _ocrModifiers, _ocrVk))
            {
                _ocrId = nextOcrId;
                _ocrRegistered = true;
            }
        }
    }

    public bool Register(AppSettings settings, out string? errorMessage)
    {
        errorMessage = null;

        if (!ValidateSettings(settings, out errorMessage))
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hwndSource == null && _useRealHwnd)
        {
            InitializeMessageSink();
        }

        IntPtr handle = _hwndSource?.Handle ?? _fakeHwnd;

        uint newEditorMod = Win32Helper.MOD_NOREPEAT;
        if (settings.HotkeyCtrl) newEditorMod |= Win32Helper.MOD_CONTROL;
        if (settings.HotkeyShift) newEditorMod |= Win32Helper.MOD_SHIFT;
        if (settings.HotkeyAlt) newEditorMod |= Win32Helper.MOD_ALT;
        if (settings.HotkeyWin) newEditorMod |= Win32Helper.MOD_WIN;
        Enum.TryParse<Key>(settings.HotkeyKey, true, out var eKey);
        uint newEditorVk = (uint)KeyInterop.VirtualKeyFromKey(eKey);

        uint newInstantMod = Win32Helper.MOD_NOREPEAT;
        if (settings.InstantHotkeyCtrl) newInstantMod |= Win32Helper.MOD_CONTROL;
        if (settings.InstantHotkeyShift) newInstantMod |= Win32Helper.MOD_SHIFT;
        if (settings.InstantHotkeyAlt) newInstantMod |= Win32Helper.MOD_ALT;
        if (settings.InstantHotkeyWin) newInstantMod |= Win32Helper.MOD_WIN;
        Enum.TryParse<Key>(settings.InstantHotkeyKey, true, out var iKey);
        uint newInstantVk = (uint)KeyInterop.VirtualKeyFromKey(iKey);

        uint newOcrMod = Win32Helper.MOD_NOREPEAT;
        if (settings.OcrHotkeyCtrl) newOcrMod |= Win32Helper.MOD_CONTROL;
        if (settings.OcrHotkeyShift) newOcrMod |= Win32Helper.MOD_SHIFT;
        if (settings.OcrHotkeyAlt) newOcrMod |= Win32Helper.MOD_ALT;
        if (settings.OcrHotkeyWin) newOcrMod |= Win32Helper.MOD_WIN;
        Enum.TryParse<Key>(settings.OcrHotkeyKey, true, out var oKey);
        uint newOcrVk = (uint)KeyInterop.VirtualKeyFromKey(oKey);

        // If chords are already registered with identical settings, return success
        if (_editorRegistered && _editorModifiers == newEditorMod && _editorVk == newEditorVk &&
            _instantSaveRegistered && _instantSaveModifiers == newInstantMod && _instantSaveVk == newInstantVk &&
            _ocrConfigured && _ocrModifiers == newOcrMod && _ocrVk == newOcrVk &&
            (_ocrSuspended || _ocrRegistered))
        {
            return true;
        }

        // Snapshot existing state for rollback in case registration of any key fails
        var prevEditor = (_editorRegistered, _editorId, _editorModifiers, _editorVk);
        var prevInstant = (_instantSaveRegistered, _instantSaveId, _instantSaveModifiers, _instantSaveVk);
        var prevOcr = (_ocrRegistered, _ocrId, _ocrModifiers, _ocrVk);

        // Unregister existing hotkeys before registering new ones to avoid ID/key conflicts
        if (_editorRegistered && handle != IntPtr.Zero)
        {
            _unregisterHotKey(handle, _editorId);
            _editorRegistered = false;
        }
        if (_instantSaveRegistered && handle != IntPtr.Zero)
        {
            _unregisterHotKey(handle, _instantSaveId);
            _instantSaveRegistered = false;
        }
        if (_ocrRegistered && handle != IntPtr.Zero)
        {
            _unregisterHotKey(handle, _ocrId);
            _ocrRegistered = false;
        }

        int nextEditorId = _editorId == 9001 ? 9002 : 9001;
        int nextInstantId = _instantSaveId == 9003 ? 9004 : 9003;
        int nextOcrId = _ocrId == 9005 ? 9006 : 9005;

        // Try registering hotkey 1 (Editor)
        bool editorOk = _registerHotKey(handle, nextEditorId, newEditorMod, newEditorVk);
        if (!editorOk)
        {
            Rollback(handle, prevEditor, prevInstant, prevOcr);
            errorMessage = "Не удалось зарегистрировать горячую клавишу открытия редактора. Возможно, она уже используется другим приложением.";
            return false;
        }

        // Try registering hotkey 2 (Instant Save)
        bool instantOk = _registerHotKey(handle, nextInstantId, newInstantMod, newInstantVk);
        if (!instantOk)
        {
            _unregisterHotKey(handle, nextEditorId);
            Rollback(handle, prevEditor, prevInstant, prevOcr);
            errorMessage = "Не удалось зарегистрировать горячую клавишу мгновенного сохранения. Возможно, она уже используется другим приложением.";
            return false;
        }

        // Try registering hotkey 3 (OCR) unless the chord is paused inside a text editor
        bool ocrOk = true;
        if (!_ocrSuspended)
        {
            ocrOk = _registerHotKey(handle, nextOcrId, newOcrMod, newOcrVk);
            if (!ocrOk)
            {
                _unregisterHotKey(handle, nextEditorId);
                _unregisterHotKey(handle, nextInstantId);
                Rollback(handle, prevEditor, prevInstant, prevOcr);
                errorMessage = "Не удалось зарегистрировать горячую клавишу снимка экрана и OCR. Возможно, она уже используется другим приложением.";
                return false;
            }
        }

        // All registered successfully
        _editorId = nextEditorId;
        _editorModifiers = newEditorMod;
        _editorVk = newEditorVk;
        _editorRegistered = true;

        _instantSaveId = nextInstantId;
        _instantSaveModifiers = newInstantMod;
        _instantSaveVk = newInstantVk;
        _instantSaveRegistered = true;

        _ocrId = nextOcrId;
        _ocrModifiers = newOcrMod;
        _ocrVk = newOcrVk;
        _ocrConfigured = true;
        _ocrRegistered = !_ocrSuspended && ocrOk;

        return true;
    }

    private void Rollback(
        IntPtr handle,
        (bool Registered, int Id, uint Modifiers, uint Vk) prevEditor,
        (bool Registered, int Id, uint Modifiers, uint Vk) prevInstant,
        (bool Registered, int Id, uint Modifiers, uint Vk) prevOcr)
    {
        if (handle == IntPtr.Zero) return;

        if (prevEditor.Registered)
        {
            if (_registerHotKey(handle, prevEditor.Id, prevEditor.Modifiers, prevEditor.Vk))
            {
                _editorId = prevEditor.Id;
                _editorModifiers = prevEditor.Modifiers;
                _editorVk = prevEditor.Vk;
                _editorRegistered = true;
            }
        }

        if (prevInstant.Registered)
        {
            if (_registerHotKey(handle, prevInstant.Id, prevInstant.Modifiers, prevInstant.Vk))
            {
                _instantSaveId = prevInstant.Id;
                _instantSaveModifiers = prevInstant.Modifiers;
                _instantSaveVk = prevInstant.Vk;
                _instantSaveRegistered = true;
            }
        }

        if (prevOcr.Registered)
        {
            if (_registerHotKey(handle, prevOcr.Id, prevOcr.Modifiers, prevOcr.Vk))
            {
                _ocrId = prevOcr.Id;
                _ocrModifiers = prevOcr.Modifiers;
                _ocrVk = prevOcr.Vk;
                _ocrRegistered = true;
            }
        }
    }

    public void Unregister()
    {
        IntPtr handle = _hwndSource?.Handle ?? _fakeHwnd;
        if (handle != IntPtr.Zero)
        {
            if (_editorRegistered)
            {
                _unregisterHotKey(handle, _editorId);
                _editorRegistered = false;
            }
            if (_instantSaveRegistered)
            {
                _unregisterHotKey(handle, _instantSaveId);
                _instantSaveRegistered = false;
            }
            if (_ocrRegistered)
            {
                _unregisterHotKey(handle, _ocrId);
                _ocrRegistered = false;
            }
        }
        _ocrConfigured = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Unregister();
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource.Dispose();
                _hwndSource = null;
            }
            _disposed = true;
        }
    }
}
