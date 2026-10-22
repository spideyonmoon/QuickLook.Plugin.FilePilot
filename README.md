# QuickLook.Plugin.FilePilot

A [QuickLook](https://github.com/QL-Win/QuickLook) plugin that makes Space in [File Pilot](https://filepilot.tech) preview files in QuickLook, the way it works in Explorer.

- **Space on a file**: opens it in QuickLook. Space again, or Esc, closes it.
- **Space on a folder**: File Pilot's own folder inspector, as usual (configurable).
- **While a preview is open**: moving the cursor (arrows, `J`/`K`, Home/End, PageUp/PageDown, clicking) switches the preview to the file under the cursor. Enter or Backspace closes it.
- **Typing in a File Pilot text box** (filter, rename, path bar): Space types a space as normal.

It runs inside QuickLook, so there's no extra program to start, and it only listens to the keyboard while File Pilot is the active window.

## Install

1. Download `QuickLook.Plugin.FilePilot.qlplugin` from [Releases](https://github.com/spideyonmoon/QuickLook.Plugin.FilePilot/releases).
2. Select it in Explorer and press Space to open it in QuickLook, then click **Install**.
3. Restart QuickLook.

To uninstall, delete `%APPDATA%\pooi.moe\QuickLook\QuickLook.Plugin\QuickLook.Plugin.FilePilot` and restart QuickLook.

## Settings

Settings live in `%APPDATA%\pooi.moe\QuickLook\QuickLook.Plugin.FilePilot.config`, which is created the first time the plugin loads. Restart QuickLook after editing it.

| Setting | Default | |
|---|---|---|
| `FolderPreview` | `FilePilot` | `QuickLook` previews folders in QuickLook too. |
| `FollowCursor` | `True` | `False` keeps showing the same file when the cursor moves. |
| `CopyAsPathHotkey` | `Ctrl+Shift+C` | Set this if you rebound *Copy as path* in File Pilot. |
| `Debug` | `False` | `True` logs each decision to `%APPDATA%\pooi.moe\QuickLook\QuickLook.Exception.log`. |

## How it works

File Pilot can't run an external tool from a hotkey, and QuickLook only knows how to read the selection in Explorer and a few other file managers. So the plugin bridges the two:

1. It starts from the plugin's `Init()` and never previews anything itself (`CanHandle` always returns false), the same shape as QuickLook's official WoW64HookHelper plugin.
2. It watches which window is in front. Only while File Pilot is, it installs a low-level keyboard and mouse hook to catch Space and cursor movement.
3. It reads the selected item by sending File Pilot's *Copy as path* hotkey, then restores your previous clipboard contents (unless another app changed the clipboard in the meantime). The round trip takes about 50–90 ms, short enough that Win+V clipboard history doesn't record the path.
4. For a file, it opens the preview through QuickLook's named pipe, the same channel `QuickLook.exe <path>` uses. For a folder, it passes the Space through to File Pilot.

## Caveats

- **Text-box detection is a heuristic.** File Pilot draws its own UI and exposes no caret or accessibility information, so the plugin guesses from your keys, based on File Pilot's default bindings: letters, F2, Ctrl+F, `/` and similar keys mean you're typing; Enter, Esc, Up/Down or a click mean you're back in the list. If Space ever previews when you meant to type a space, press Esc and try again.
- **It relies on *Copy as path*.** If you remove or rebind that hotkey in File Pilot, update `CopyAsPathHotkey`.

## Building

GitHub Actions builds every push and attaches the `.qlplugin` to tagged releases. To build locally you need the .NET SDK (8 or newer):

```bat
git clone --recursive https://github.com/spideyonmoon/QuickLook.Plugin.FilePilot
cd QuickLook.Plugin.FilePilot
powershell -File Scripts\update-version.ps1
dotnet build QuickLook.Plugin.FilePilot.sln -c Release
powershell -File Scripts\pack-zip.ps1
```

## See also

- [QuickLook wiki: File Pilot](https://github.com/QL-Win/QuickLook/wiki/File-Pilot), an AutoHotkey script that takes the same approach.
