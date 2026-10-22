using System;
using System.Collections.Generic;
using System.Windows.Forms;
using QuickLook.Common.Helpers;

namespace QuickLook.Plugin.FilePilot;

/// <summary>
/// User settings, stored by QuickLook in %APPDATA%\pooi.moe\QuickLook\QuickLook.Plugin.FilePilot.config.
/// Every key is written back on load so the file lists all available options.
/// </summary>
internal static class Settings
{
    private const string Domain = "QuickLook.Plugin.FilePilot";

    /// <summary>Space on a folder opens QuickLook instead of File Pilot's own inspector.</summary>
    public static bool FoldersInQuickLook { get; private set; }

    /// <summary>While a preview is open, moving File Pilot's cursor switches the preview.</summary>
    public static bool FollowCursor { get; private set; } = true;

    /// <summary>The File Pilot hotkey bound to "Copy as path", used to read the selection.</summary>
    public static Keys[] CopyAsPathKeys { get; private set; } = DefaultCopyAsPath;

    /// <summary>
    /// The File Pilot hotkey bound to "Toggle cursor selection". "Copy as path" copies the current
    /// folder when nothing is selected, so this is used to select the item under the cursor instead.
    /// </summary>
    public static Keys[] ToggleCursorSelectionKeys { get; private set; } = DefaultToggleCursorSelection;

    /// <summary>Writes each preview decision to QuickLook's log.</summary>
    public static bool Debug { get; private set; }

    private static Keys[] DefaultCopyAsPath => [Keys.ControlKey, Keys.ShiftKey, Keys.C];

    private static Keys[] DefaultToggleCursorSelection => [Keys.ControlKey, Keys.Space];

    public static void Load()
    {
        var folders = Read("FolderPreview", "FilePilot");
        FoldersInQuickLook = folders.Equals("QuickLook", StringComparison.OrdinalIgnoreCase);

        FollowCursor = Read("FollowCursor", true);

        var hotkey = Read("CopyAsPathHotkey", "Ctrl+Shift+C");
        CopyAsPathKeys = ParseHotkey(hotkey) ?? DefaultCopyAsPath;

        var toggle = Read("ToggleCursorSelectionHotkey", "Ctrl+Space");
        ToggleCursorSelectionKeys = ParseHotkey(toggle) ?? DefaultToggleCursorSelection;

        Debug = Read("Debug", false);
    }

    private static T Read<T>(string id, T fallback)
    {
        var value = SettingHelper.Get(id, fallback, Domain);
        SettingHelper.Set(id, value, Domain);
        return value;
    }

    /// <summary>Parses "Ctrl+Shift+C" style hotkeys into the keys to press, modifiers first.</summary>
    private static Keys[] ParseHotkey(string text)
    {
        var keys = new List<Keys>();
        foreach (var part in text.Split('+'))
        {
            var token = part.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl": case "control": keys.Add(Keys.ControlKey); continue;
                case "shift": keys.Add(Keys.ShiftKey); continue;
                case "alt": keys.Add(Keys.Menu); continue;
                case "win": keys.Add(Keys.LWin); continue;
            }

            if (token.Length == 1 && char.IsDigit(token[0]))
                token = "D" + token;
            if (!Enum.TryParse(token, true, out Keys key))
                return null;
            keys.Add(key);
        }

        return keys.Count > 0 ? keys.ToArray() : null;
    }
}
