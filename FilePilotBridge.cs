using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using QuickLook.Common.Helpers;
using static QuickLook.Plugin.FilePilot.NativeMethods;

namespace QuickLook.Plugin.FilePilot;

/// <summary>
/// Hands File Pilot's Space key over to QuickLook.
/// <list type="bullet">
/// <item>Space on a file opens it in QuickLook; Space or Esc closes it again.</item>
/// <item>Space on a folder goes to File Pilot's own inspector (unless FolderPreview=QuickLook).</item>
/// <item>While a preview is open, moving File Pilot's cursor switches the preview.</item>
/// </list>
/// Keyboard and mouse hooks are installed only while File Pilot is the foreground window,
/// so input to every other application never passes through this code.
/// </summary>
internal static class FilePilotBridge
{
    private const string FilePilotExe = "FPilot.exe";

    /// <summary>Tags input we inject ourselves, so the hooks let it through untouched.</summary>
    private static readonly IntPtr InjectedMarker = (IntPtr)0x51554C4B; // "QULK"

    /// <summary>Opening a preview takes a moment; until then the viewer may not be visible yet.</summary>
    private static readonly TimeSpan OpenGracePeriod = TimeSpan.FromSeconds(1);

    // Slow work (clipboard round trips, pipe writes, waits) runs here, never on the hook thread:
    // a low-level hook that doesn't return quickly stalls all input and gets removed by Windows.
    private static readonly BlockingCollection<Action> Work = new();

    private static Thread _hookThread;

    // Hook-thread state.
    private static WinEventProc _foregroundProc;
    private static LowLevelProc _keyProc, _mouseProc; // kept referenced so the GC can't collect them
    private static IntPtr _keyHook, _mouseHook;
    private static uint _filePilotPid;
    private static bool _spaceSwallowed; // we ate a Space keydown, so eat its keyup too
    private static bool _textMode;       // File Pilot is probably in a text box

    // Shared between the hook thread and the worker.
    private static volatile bool _busy;        // a Space is queued or being handled
    private static volatile bool _previewOpen; // we opened a preview and haven't closed it
    private static int _followGeneration;
    private static string _lastPreviewPath;
    private static DateTime _openedAt;

    public static void Start()
    {
        if (_hookThread != null)
            return;

        var worker = new Thread(() =>
        {
            foreach (var action in Work.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    ProcessHelper.WriteLog(e.ToString());
                }
            }
        }) { IsBackground = true, Name = "FilePilot worker" };
        worker.SetApartmentState(ApartmentState.STA); // required by the clipboard
        worker.Start();

        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "FilePilot hooks" };
        _hookThread.Start();
    }

    // ---------------- hook thread ----------------

    private static void HookThreadMain()
    {
        _foregroundProc = (_, _, hwnd, _, _, _, _) => UpdateHooks(hwnd);
        _keyProc = KeyboardHook;
        _mouseProc = MouseHook;

        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT);
        UpdateHooks(GetForegroundWindow());

        // Out-of-context WinEvents and low-level hooks are delivered through this thread's message loop.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static void UpdateHooks(IntPtr foreground)
    {
        GetWindowThreadProcessId(foreground, out var pid);

        if (pid != 0 && IsFilePilot(pid))
        {
            _filePilotPid = pid;
            if (_keyHook != IntPtr.Zero)
                return;

            var module = GetModuleHandle(null);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _textMode = false;
            Log("File Pilot focused, hooks installed");
        }
        else if (_keyHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyHook);
            UnhookWindowsHookEx(_mouseHook);
            _keyHook = _mouseHook = IntPtr.Zero;
            _spaceSwallowed = false;
            Log("File Pilot lost focus, hooks removed");
        }
    }

    private static IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.dwExtraInfo != InjectedMarker)
            {
                var key = (Keys)info.vkCode;
                var message = wParam.ToInt32();
                var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;

                if (!down)
                {
                    if (key == Keys.Space && _spaceSwallowed)
                    {
                        _spaceSwallowed = false;
                        return (IntPtr)1;
                    }
                }
                else if (FilePilotIsForeground() && OnKeyDown(key))
                {
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    private static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
            {
                _textMode = false;
                if (_previewOpen && message == WM_LBUTTONDOWN && FilePilotIsForeground())
                    QueueFollowCursor(80); // a click can move the cursor too
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    /// <summary>Returns true to swallow the key.</summary>
    private static bool OnKeyDown(Keys key)
    {
        if (key == Keys.Space && !AnyModifier())
        {
            if (_spaceSwallowed)
                return true; // auto-repeat while held
            if (_textMode && !_previewOpen)
                return false; // typing a space into a File Pilot text box

            _spaceSwallowed = true;
            _busy = true;
            Work.Add(OnSpace);
            return true;
        }

        TrackTextMode(key);

        if (_previewOpen)
        {
            if (key is Keys.Escape or Keys.Enter or Keys.Back)
            {
                // Esc dismisses; Enter and Backspace open the item or leave the folder.
                _previewOpen = false;
                Work.Add(() => QuickLookViewer.Send(QuickLookViewer.Close));
            }
            else if (Settings.FollowCursor && IsNavigationKey(key))
            {
                QueueFollowCursor(40);
            }
        }

        return false;
    }

    /// <summary>
    /// Best-effort guess whether keystrokes are going into a File Pilot text box (filter, rename,
    /// path bar...). File Pilot draws its own UI and exposes no caret or accessibility info, so this
    /// follows its default bindings: these keys open a text box, and Enter, Esc, Tab, Up/Down or a
    /// click leave it.
    /// </summary>
    private static void TrackTextMode(Keys key)
    {
        bool ctrl = Pressed(Keys.ControlKey), shift = Pressed(Keys.ShiftKey), alt = Pressed(Keys.Menu);

        switch (key)
        {
            case Keys.Enter or Keys.Escape or Keys.Tab or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown:
                _textMode = false;
                return;
            case Keys.F2 or Keys.F3 or Keys.F4 or Keys.OemQuestion when !alt:
                _textMode = true;
                return;
        }

        if (ctrl && !alt)
        {
            // Ctrl+F filter, Ctrl+L path, Ctrl+P go to, Ctrl+, options, Ctrl+Shift+E/N/F path/new folder/search.
            if (key is Keys.F or Keys.L or Keys.P or Keys.Oemcomma || shift && key is Keys.E or Keys.N)
                _textMode = true;
            return;
        }

        if (alt)
        {
            if (!ctrl && key is Keys.D or Keys.F) // Alt+D path bar, Alt+F quick access filter
                _textMode = true;
            return;
        }

        var letter = key is >= Keys.A and <= Keys.Z;
        var digit = key is >= Keys.D0 and <= Keys.D9;

        // Plain letters and digits start File Pilot's type-to-filter, except those bound as hotkeys
        // (HJKL navigation, G/Z jumps, Y/P copy-paste, I rename, Shift+F inspector filter).
        if (shift)
        {
            if (key == Keys.F || letter && "GHJKL".IndexOf((char)key) < 0)
                _textMode = true;
        }
        else if (digit || letter && "HJKLGYPZ".IndexOf((char)key) < 0)
        {
            _textMode = true;
        }
    }

    private static bool IsNavigationKey(Keys key)
    {
        if (key is Keys.Up or Keys.Down)
            return true; // Ctrl+Up/Down move the cursor without selecting; still worth following
        if (Pressed(Keys.ControlKey) || Pressed(Keys.Menu) || Pressed(Keys.LWin) || Pressed(Keys.RWin))
            return false;
        return key is Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
            or Keys.H or Keys.J or Keys.K or Keys.L or Keys.G;
    }

    private static void QueueFollowCursor(int delayMs)
    {
        var generation = Interlocked.Increment(ref _followGeneration);
        Work.Add(() => FollowCursor(generation, delayMs));
    }

    // ---------------- worker thread ----------------

    private static void OnSpace()
    {
        try
        {
            if (_previewOpen)
            {
                _previewOpen = false;
                if (QuickLookViewer.IsOpen() || DateTime.UtcNow - _openedAt < OpenGracePeriod)
                {
                    QuickLookViewer.Send(QuickLookViewer.Close);
                    Log("space: close");
                    return;
                }
                // The preview was closed from QuickLook's side; treat this Space as a fresh one.
            }

            var path = SelectionReader.Read(Settings.CopyAsPathKeys, InjectedMarker);
            Log("space: " + (path ?? "<no selection>"));

            if (path == null || Directory.Exists(path) && !Settings.FoldersInQuickLook)
            {
                InjectSpace(); // let File Pilot's own inspector handle it
                return;
            }

            QuickLookViewer.Send(QuickLookViewer.Toggle, path);
            _previewOpen = true;
            _lastPreviewPath = path;
            _openedAt = DateTime.UtcNow;
        }
        finally
        {
            _busy = false;
        }
    }

    private static void FollowCursor(int generation, int delayMs)
    {
        // Holding an arrow key queues a request per repeat; only the newest one does any work.
        if (generation != Volatile.Read(ref _followGeneration) || !_previewOpen || _busy)
            return;

        Thread.Sleep(delayMs); // let File Pilot move its cursor first
        if (generation != Volatile.Read(ref _followGeneration) || !_previewOpen || _busy)
            return;

        if (!QuickLookViewer.IsOpen() && DateTime.UtcNow - _openedAt > OpenGracePeriod)
        {
            _previewOpen = false; // closed from QuickLook's side
            return;
        }

        var path = SelectionReader.Read(Settings.CopyAsPathKeys, InjectedMarker);
        if (path == null || path == _lastPreviewPath)
            return;
        if (Directory.Exists(path) && !Settings.FoldersInQuickLook)
            return; // keep showing the last file

        Log("follow: " + path);
        QuickLookViewer.Send(QuickLookViewer.Switch, path);
        _lastPreviewPath = path;
    }

    // ---------------- helpers ----------------

    private static bool IsFilePilot(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return false;
        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) &&
                   Path.GetFileName(name.ToString()).Equals(FilePilotExe, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool FilePilotIsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == _filePilotPid;
    }

    private static bool Pressed(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static bool AnyModifier() =>
        Pressed(Keys.ControlKey) || Pressed(Keys.ShiftKey) || Pressed(Keys.Menu) ||
        Pressed(Keys.LWin) || Pressed(Keys.RWin);

    private static void InjectSpace() => Input.Press(InjectedMarker, Keys.Space);

    private static void Log(string message)
    {
        if (Settings.Debug)
            ProcessHelper.WriteLog("[FilePilot] " + message);
    }
}
