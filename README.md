# fpilot-quicklook

Use [QuickLook](https://github.com/QL-Win/QuickLook) for Space previews in [File Pilot](https://filepilot.tech), the way it works in Explorer.

- **Space on a file**: opens it in QuickLook. Space again, or Esc, closes it.
- **Space on a folder**: File Pilot's own folder inspector, as usual.
- **While a preview is open**: moving the cursor (arrows, `J`/`K`, Home/End, PageUp/PageDown, clicking) switches the preview to the file under the cursor. Enter or Backspace closes it.
- **Typing in a File Pilot text box** (filter, rename, path bar): Space types a space as normal.

It only acts while File Pilot is the active window. Everywhere else, your keyboard is untouched.

## Install

Requirements: Windows 10/11, File Pilot, and QuickLook running in the background.

1. Build it. No SDK needed; this uses the C# compiler that ships with Windows:
   ```bat
   build.cmd
   ```
2. Run `FPilotQuickLook.exe`. It has no window and sits in the background.
3. To start it with Windows, put a shortcut to it in `shell:startup`.

To stop it, end `FPilotQuickLook.exe` in Task Manager and remove the Startup shortcut.

## How it works

File Pilot can't launch external tools from a hotkey, and QuickLook only knows how to read Explorer's selection. So this is a small bridge between the two:

1. A low-level keyboard hook catches Space while File Pilot is in the foreground.
2. It reads the selected item by sending File Pilot's *Copy as path* hotkey (`Ctrl+Shift+C`), then puts your previous clipboard contents back. The whole round trip takes about 50–90 ms. The path is replaced quickly enough that it doesn't show up in Win+V clipboard history.
3. For a file, it tells QuickLook to open it through QuickLook's named pipe (the same one `QuickLook.exe <path>` uses). For a folder, it passes the Space through to File Pilot.

## Caveats

- **Text-box detection is a heuristic.** File Pilot draws its own UI, so there's no reliable way to tell when a text box has focus. The tool guesses from your recent keys: letters, F2, Ctrl+F, `/` and similar keys mean you're typing; Enter, Esc, arrows or a click mean you're back in the list. If Space ever previews when you meant to type a space, press Esc and try again.
- **It assumes File Pilot's default `Ctrl+Shift+C` (Copy as path) binding.** If you rebound it, change `SendChord(...)` in `GetSelectedPath`.
- **Memory:** about 25 MB, mostly the .NET Framework runtime. A native C rewrite could cut that to roughly 1–2 MB.

## Debugging

Run `FPilotQuickLook.exe --log` to write each preview decision (which path it read, and the timings) to `%TEMP%\FPilotQuickLook.log`.
