using Avalonia;
using Avalonia.Controls;

namespace HarmonyRemote.App;

internal static class Program
{
    /// <summary><c>--show</c>: open the remote right away instead of waiting for a tray click.</summary>
    public static bool ShowOnStart { get; private set; }

    /// <summary>The first-instance guard; <see cref="App"/> listens on it for "show" requests from later launches.</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ShowOnStart = args.Contains("--show");
        Instance = SingleInstance.TryAcquire();
        if (Instance is null)
            return 0; // already running: it has been asked to open the remote
        using (Instance)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
