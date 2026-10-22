// FPilotQuickLook - routes File Pilot's Space "glance" to QuickLook.
//
// Space on a file   -> QuickLook preview (toggle)
// Space on a folder -> File Pilot's own inspector (Space passed through)
// While QuickLook is open, moving the cursor in File Pilot switches the preview,
// and Esc closes it.
//
// The selection is read by sending File Pilot's "copy as path" hotkey
// (Ctrl+Shift+C) and restoring the clipboard afterwards.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Collections.Concurrent;
using System.Windows.Forms;

static class Program
{
    const string FilePilotExe = "FPilot";
    const int Marker = 0x51554C4B; // "QULK" tags our own injected input
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "FPilotQuickLook.log");
    static readonly bool Verbose = Environment.GetCommandLineArgs().Length > 1 &&
                                   Environment.GetCommandLineArgs()[1] == "--log";

    static IntPtr keyHook, mouseHook;
    static Native.LowLevelProc keyProc, mouseProc; // keep delegates alive
    // Hooks live on the main thread, which must never block; all slow work
    // (clipboard, pipe, sleeps) runs on this STA worker.
    static readonly BlockingCollection<Action> work = new BlockingCollection<Action>();

    static bool spaceSwallowed;            // we ate a Space keydown; eat its keyup too
    static volatile bool busy;
    static bool textMode;                  // File Pilot is (probably) in a text field
    static volatile bool previewOpen;      // we opened QuickLook and haven't closed it
    static string lastPreviewPath;

    [STAThread]
    static void Main()
    {
        bool created;
        using (new Mutex(true, "FPilotQuickLook.SingleInstance", out created))
        {
            if (!created) return;
            var worker = new Thread(() => { foreach (var a in work.GetConsumingEnumerable()) a(); }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            IntPtr mod = Native.GetModuleHandle(null);
            keyProc = KeyHook;
            mouseProc = MouseHook;
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            Log("started");
            Application.Run();
        }
    }

    // ---------------- hooks ----------------

    static IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            bool ours = k.dwExtraInfo == (IntPtr)Marker;

            Keys key = (Keys)k.vkCode;
            // Release a swallowed Space wherever focus went in the meantime.
            if (!ours && up && key == Keys.Space && spaceSwallowed) { spaceSwallowed = false; return (IntPtr)1; }

            if (!ours && down && FilePilotIsForeground())
            {
                if (key == Keys.Space && !AnyModifier())
                {
                    if (spaceSwallowed) return (IntPtr)1; // auto-repeat
                    if (!textMode || previewOpen)
                    {
                        spaceSwallowed = true;
                        busy = true; // makes queued cursor-follows skip; OnSpace clears it
                        work.Add(OnSpace);
                        return (IntPtr)1;
                    }
                }
                else
                {
                    TrackTextMode(key);
                    if (previewOpen)
                    {
                        if (key == Keys.Escape || key == Keys.Enter || key == Keys.Back)
                        {
                            // Esc dismisses; Enter/Backspace leave the current folder or open the file.
                            previewOpen = false;
                            work.Add(() => SendToQuickLook("Close", ""));
                        }
                        else if (IsNavigationKey(key)) work.Add(FollowCursor);
                    }
                }
            }
        }
        return Native.CallNextHookEx(keyHook, code, wParam, lParam);
    }

    static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        int msg = wParam.ToInt32();
        if (code >= 0 && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN))
        {
            textMode = false;
            if (previewOpen && msg == Native.WM_LBUTTONDOWN && FilePilotIsForeground())
                work.Add(() => { Thread.Sleep(60); FollowCursor(); });
        }
        return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    // Best-effort guess whether keystrokes are going into a File Pilot text box
    // (filter, rename, path bar...), so a typed space isn't hijacked.
    static void TrackTextMode(Keys key)
    {
        bool ctrl = Pressed(Keys.ControlKey), shift = Pressed(Keys.ShiftKey), alt = Pressed(Keys.Menu);
        switch (key)
        {
            case Keys.Enter: case Keys.Escape: case Keys.Tab:
            case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown:
                textMode = false; return;
            case Keys.F2: case Keys.F3: case Keys.F4: case Keys.OemQuestion:
                if (!alt) textMode = true; return;
        }
        if (ctrl && !alt && (key == Keys.F || key == Keys.L || key == Keys.P || key == Keys.E && shift || key == Keys.N && shift || key == Keys.Oemcomma)) { textMode = true; return; }
        if (alt && !ctrl && (key == Keys.D || key == Keys.F)) { textMode = true; return; }
        if (ctrl || alt) return;
        // Plain letters/digits start File Pilot's type-to-filter, except the ones bound as hotkeys.
        bool letter = key >= Keys.A && key <= Keys.Z, digit = key >= Keys.D0 && key <= Keys.D9;
        if (shift && key == Keys.F) { textMode = true; return; }
        if (shift) { if (letter && "GHJKL".IndexOf((char)key) < 0) textMode = true; return; }
        if (digit || letter && "HJKLGYPZ".IndexOf((char)key) < 0) textMode = true;
    }

    static bool IsNavigationKey(Keys key)
    {
        if (AnyModifierExceptShift() && key != Keys.Up && key != Keys.Down) return false;
        switch (key)
        {
            case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
            case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
            case Keys.J: case Keys.K: case Keys.H: case Keys.L: case Keys.G:
                return true;
        }
        return false;
    }

    // ---------------- actions ----------------

    static void OnSpace()
    {
        try
        {
            if (previewOpen)
            {
                previewOpen = false;
                SendToQuickLook("Close", "");
                Log("space -> close");
                return;
            }
            string path = GetSelectedPath();
            Log("space -> " + (path ?? "<none>"));
            if (path == null || Directory.Exists(path))
            {
                // Folder (or nothing we can read): let File Pilot's own inspector handle it.
                InjectSpace();
                return;
            }
            SendToQuickLook("Toggle", path);
            previewOpen = true;
            lastPreviewPath = path;
        }
        catch (Exception e) { Log("OnSpace: " + e); }
        finally { busy = false; }
    }

    static void FollowCursor()
    {
        if (!previewOpen || busy) return;
        busy = true;
        try
        {
            Thread.Sleep(40); // let File Pilot move its cursor first
            if (!QuickLookVisible()) { previewOpen = false; return; }
            string path = GetSelectedPath();
            if (path == null || path == lastPreviewPath) return;
            if (Directory.Exists(path)) return; // keep the last file shown
            SendToQuickLook("Switch", path);
            lastPreviewPath = path;
        }
        catch (Exception e) { Log("FollowCursor: " + e); }
        finally { busy = false; }
    }

    static string GetSelectedPath()
    {
        var sw = Stopwatch.StartNew();
        IDataObject saved = SnapshotClipboard();
        Log("  snapshot " + sw.ElapsedMilliseconds + "ms");
        string result = null;
        try
        {
            uint before = Native.GetClipboardSequenceNumber();
            SendChord(Keys.ControlKey, Keys.ShiftKey, Keys.C);
            for (int i = 0; i < 40 && Native.GetClipboardSequenceNumber() == before; i++) Thread.Sleep(10);
            Log("  copied " + sw.ElapsedMilliseconds + "ms");
            if (Native.GetClipboardSequenceNumber() == before) return null;
            Thread.Sleep(10);
            string text = null;
            for (int i = 0; i < 5 && text == null; i++)
            {
                try { text = Clipboard.GetText(); } catch { Thread.Sleep(15); }
            }
            if (!string.IsNullOrEmpty(text))
            {
                string first = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim().Trim('"');
                if (File.Exists(first) || Directory.Exists(first)) result = first;
            }
        }
        finally { RestoreClipboard(saved); Log("  restored " + sw.ElapsedMilliseconds + "ms -> " + result); }
        return result;
    }

    // ---------------- clipboard ----------------

    static IDataObject SnapshotClipboard()
    {
        try
        {
            IDataObject live = Clipboard.GetDataObject();
            if (live == null) return null;
            var copy = new DataObject();
            foreach (string f in live.GetFormats(false))
            {
                try { object d = live.GetData(f, false); if (d != null) copy.SetData(f, false, d); } catch { }
            }
            return copy;
        }
        catch { return null; }
    }

    static void RestoreClipboard(IDataObject saved)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (saved == null || saved.GetFormats().Length == 0) Clipboard.Clear();
                else Clipboard.SetDataObject(saved, true);
                return;
            }
            catch { Thread.Sleep(20); }
        }
    }

    // ---------------- QuickLook ----------------

    static void SendToQuickLook(string message, string path)
    {
        string pipe = "QuickLook.App.Pipe." + WindowsIdentity.GetCurrent().User.Value;
        try
        {
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out))
            {
                client.Connect(500);
                using (var w = new StreamWriter(client)) w.WriteLine("QuickLook.App.PipeMessages." + message + "|" + path);
            }
        }
        catch (Exception e) { Log("QuickLook pipe: " + e.Message + " (is QuickLook running?)"); }
    }

    static bool QuickLookVisible()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("QuickLook")) pids.Add((uint)p.Id);
        bool found = false;
        Native.EnumWindows((h, l) =>
        {
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (pids.Contains(pid) && Native.IsWindowVisible(h))
            {
                var t = new StringBuilder(64); Native.GetWindowText(h, t, 64);
                if (t.ToString().StartsWith("QuickLook - ")) { found = true; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------------- input helpers ----------------

    static bool FilePilotIsForeground()
    {
        uint pid; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        try { return Process.GetProcessById((int)pid).ProcessName.Equals(FilePilotExe, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static bool Pressed(Keys k) { return (Native.GetAsyncKeyState((int)k) & 0x8000) != 0; }
    static bool AnyModifier() { return Pressed(Keys.ControlKey) || Pressed(Keys.ShiftKey) || Pressed(Keys.Menu) || Pressed(Keys.LWin) || Pressed(Keys.RWin); }
    static bool AnyModifierExceptShift() { return Pressed(Keys.ControlKey) || Pressed(Keys.Menu) || Pressed(Keys.LWin) || Pressed(Keys.RWin); }

    static void InjectSpace() { SendKeys_(new[] { Key(Keys.Space, false), Key(Keys.Space, true) }); }

    static void SendChord(params Keys[] keys)
    {
        var list = new List<Native.INPUT>();
        foreach (var k in keys) list.Add(Key(k, false));
        for (int i = keys.Length - 1; i >= 0; i--) list.Add(Key(keys[i], true));
        SendKeys_(list.ToArray());
    }

    static Native.INPUT Key(Keys k, bool up)
    {
        var i = new Native.INPUT { type = 1 };
        i.ki.wVk = (ushort)k;
        i.ki.dwFlags = up ? 2u : 0u;
        i.ki.dwExtraInfo = (IntPtr)Marker;
        return i;
    }

    static void SendKeys_(Native.INPUT[] inputs) { Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT))); }

    static void Log(string s)
    {
        if (!Verbose) return;
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }
}

static class Native
{
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204;

    public delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi; // sizes the union correctly on x64
    }

    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc fn, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
