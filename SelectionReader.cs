using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static QuickLook.Plugin.FilePilot.NativeMethods;

namespace QuickLook.Plugin.FilePilot;

/// <summary>
/// Reads File Pilot's selection by pressing its "Copy as path" hotkey, then restores the clipboard.
/// File Pilot offers no other way to get it. The path is on the clipboard for a few tens of
/// milliseconds, which is too short for Windows clipboard history to record it.
/// Must be called on an STA thread.
/// </summary>
internal static class SelectionReader
{
    /// <summary>
    /// Returns the item to preview: the first selected path, or the item under File Pilot's cursor
    /// when nothing is selected. Null if File Pilot didn't copy anything usable.
    /// </summary>
    public static string Read(IntPtr marker, Action<string> log)
    {
        var saved = Snapshot();
        var ours = 0u; // clipboard sequence number after our last copy; 0 = clipboard untouched

        try
        {
            var first = CopyAsPath(marker, log, ref ours);
            if (first == null || !Directory.Exists(first))
                return first; // a selected file, or nothing usable

            // A folder is ambiguous: either a folder is selected, or nothing is and File Pilot copied
            // the folder it's showing. Select the cursor item, copy again, then undo the selection
            // change. If the second path is inside the first, nothing was selected and the second
            // path is the cursor item.
            Input.Press(marker, Settings.ToggleCursorSelectionKeys);
            var second = CopyAsPath(marker, log, ref ours);
            Input.Press(marker, Settings.ToggleCursorSelectionKeys);

            if (second != null && IsDirectChild(second, first))
            {
                log("nothing selected; cursor item is " + second);
                return second;
            }

            return first;
        }
        finally
        {
            // Restore unless something else has written to the clipboard in the meantime.
            if (ours != 0 && GetClipboardSequenceNumber() == ours)
                Restore(saved);
        }
    }

    /// <summary>Presses "Copy as path" and returns the first path File Pilot copied.</summary>
    private static string CopyAsPath(IntPtr marker, Action<string> log, ref uint ours)
    {
        // File Pilot occasionally fails to copy, presumably when another app still has the clipboard
        // open; one retry covers that.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var before = GetClipboardSequenceNumber();
            var started = DateTime.UtcNow;
            Input.Press(marker, Settings.CopyAsPathKeys);

            if (!WaitForChange(before, TimeSpan.FromMilliseconds(300)))
            {
                log($"copy attempt {attempt}: clipboard unchanged after 300 ms");
                continue;
            }

            var text = ReadText();
            ours = GetClipboardSequenceNumber();

            var first = text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim().Trim('"');
            if (first != null && (File.Exists(first) || Directory.Exists(first)))
                return first;

            log($"copy attempt {attempt}: unusable text '{text}' after {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
            return null; // File Pilot did copy, just not a file system path (e.g. a virtual folder)
        }

        return null;
    }

    private static bool IsDirectChild(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path.TrimEnd('\\'))?.TrimEnd('\\'), folder.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static bool WaitForChange(uint before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (GetClipboardSequenceNumber() == before)
        {
            if (DateTime.UtcNow > deadline)
                return false;
            Thread.Sleep(5);
        }

        return true;
    }

    private static string ReadText()
    {
        // The sequence number changes when File Pilot empties the clipboard, slightly before it
        // puts the text there, and the clipboard may still be held open; retry briefly.
        for (var i = 0; i < 20; i++)
        {
            try
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            catch (ExternalException)
            {
            }

            Thread.Sleep(10);
        }

        return null;
    }

    private static DataObject Snapshot()
    {
        try
        {
            var live = Clipboard.GetDataObject();
            var formats = live?.GetFormats(false);
            if (formats == null || formats.Length == 0)
                return null;

            var copy = new DataObject();
            foreach (var format in formats)
            {
                try
                {
                    var data = live.GetData(format, false);
                    if (data != null)
                        copy.SetData(format, false, data);
                }
                catch
                {
                    // Some formats can't be read back (e.g. delay-rendered by a closed app); skip them.
                }
            }

            return copy;
        }
        catch
        {
            return null;
        }
    }

    private static void Restore(DataObject saved)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (saved == null)
                    Clipboard.Clear();
                else
                    Clipboard.SetDataObject(saved, true);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }
    }
}

internal static class Input
{
    /// <summary>Presses a key chord (all keys down in order, then up in reverse), tagged with <paramref name="marker"/>.</summary>
    public static void Press(IntPtr marker, params Keys[] chord)
    {
        var inputs = chord.Select(k => Key(k, false, marker))
            .Concat(chord.Reverse().Select(k => Key(k, true, marker)))
            .ToArray();

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(Keys key, bool up, IntPtr marker) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = (ushort)key,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                dwExtraInfo = marker,
            },
        },
    };
}
