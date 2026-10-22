using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using QuickLook.Common.Helpers;

namespace QuickLook.Plugin.FilePilot;

/// <summary>
/// Drives QuickLook's preview window through its named pipe, the same channel
/// <c>QuickLook.exe &lt;path&gt;</c> uses. That's QuickLook's public, stable interface,
/// unlike its internal classes.
/// </summary>
internal static class QuickLookViewer
{
    public const string Toggle = "QuickLook.App.PipeMessages.Toggle";
    public const string Switch = "QuickLook.App.PipeMessages.Switch";
    public const string Close = "QuickLook.App.PipeMessages.Close";

    private static readonly Lazy<string> PipeName = new(() =>
    {
        // Read it from QuickLook itself in case the naming ever changes; fall back to the known format.
        var field = Assembly.GetEntryAssembly()?.GetType("QuickLook.PipeServerManager")
            ?.GetField("PipeName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(null) as string
               ?? "QuickLook.App.Pipe." + WindowsIdentity.GetCurrent().User?.Value;
    });

    public static void Send(string message, string path = "")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName.Value, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(message + "|" + path);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog("[FilePilot] QuickLook pipe: " + e.Message);
        }
    }

    /// <summary>Whether a QuickLook preview window is currently showing.</summary>
    public static bool IsOpen()
    {
        var app = Application.Current;
        if (app == null)
            return false;

        return app.Dispatcher.Invoke(() =>
        {
            var quickLook = Assembly.GetEntryAssembly();
            var windows = app.Windows.OfType<Window>().Where(w => w.GetType().Assembly == quickLook).ToList();
            var viewers = windows.Where(w => w.GetType().Name.Contains("Viewer")).ToList();
            return (viewers.Count > 0 ? viewers : windows).Any(w => w.IsVisible);
        });
    }
}
