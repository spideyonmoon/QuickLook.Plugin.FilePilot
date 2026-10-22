using QuickLook.Common.Plugin;

namespace QuickLook.Plugin.FilePilot;

/// <summary>
/// A background-only plugin: it never previews files itself. <see cref="Init"/> starts
/// <see cref="FilePilotBridge"/>, which hands File Pilot's Space key over to QuickLook.
/// </summary>
public class Plugin : IViewer
{
    public int Priority => int.MinValue;

    public void Init()
    {
        Settings.Load();
        FilePilotBridge.Start();
    }

    public bool CanHandle(string path) => false;

    public void Prepare(string path, ContextObject context)
    {
    }

    public void View(string path, ContextObject context)
    {
    }

    public void Cleanup()
    {
    }
}
