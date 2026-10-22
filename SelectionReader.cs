using System;
using System.IO;
using System.Linq;
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
    /// <summary>Returns the first selected path, or null if File Pilot didn't copy one.</summary>
    public static string Read(Keys[] copyAsPathKeys, IntPtr marker)
    {
        var saved = Snapshot();
        var before = GetClipboardSequenceNumber();

        Input.Press(marker, copyAsPathKeys);

        if (!WaitForChange(before, TimeSpan.FromMilliseconds(400)))
            return null; // nothing copied (e.g. File Pilot is in a text box); clipboard untouched
        var ours = GetClipboardSequenceNumber();

        try
        {
            var text = ReadText();
            if (string.IsNullOrEmpty(text))
                return null;

            var first = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim().Trim('"');
            return first != null && (File.Exists(first) || Directory.Exists(first)) ? first : null;
        }
        finally
        {
            // Restore unless something else has written to the clipboard in the meantime.
            if (GetClipboardSequenceNumber() == ours)
                Restore(saved);
        }
    }

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
        for (var i = 0; i < 10; i++)
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

        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
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
