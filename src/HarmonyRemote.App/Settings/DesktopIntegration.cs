using System.Text.RegularExpressions;

namespace HarmonyRemote.App.Settings;

/// <summary>Linux desktop glue: start-at-login and finding which screen edge holds the system tray.</summary>
public static partial class DesktopIntegration
{
    private static string AutostartFile =>
        Path.Combine(Sandbox.HostConfigDirectory, "autostart", "harmony-remote.desktop");

    /// <summary>
    /// Whether the app starts at login. Outside Flatpak the autostart file is the truth; inside, the sandbox can't see
    /// it, so the last answer the Background portal gave is kept in the settings.
    /// </summary>
    public static bool IsAutostartEnabled(AppSettings settings) =>
        Sandbox.IsFlatpak ? settings.StartAtLogin : File.Exists(AutostartFile);

    /// <summary>Turns start-at-login on or off. Throws <see cref="InvalidOperationException"/> when the portal says no.</summary>
    public static async Task SetAutostartAsync(bool enabled)
    {
        if (Sandbox.IsFlatpak)
        {
            if (!await BackgroundPortal.RequestAutostartAsync(enabled, ["harmony-remote"]))
                throw new InvalidOperationException("the desktop didn't allow it");
            return;
        }

        if (!enabled)
        {
            File.Delete(AutostartFile);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        await File.WriteAllTextAsync(AutostartFile,
            $"""
            [Desktop Entry]
            Type=Application
            Name=Harmony Remote
            Comment=Control your Logitech Harmony hub from the system tray
            Exec={LaunchCommand()}
            Icon=harmony-remote
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """ + "\n");
    }

    /// <summary>
    /// The command that starts this build: the apphost when published, otherwise <c>dotnet harmony-remote.dll</c>
    /// (when run via <c>dotnet</c>, <see cref="Environment.ProcessPath"/> is the dotnet host itself).
    /// </summary>
    private static string LaunchCommand()
    {
        string process = Environment.ProcessPath ?? "harmony-remote";
        bool viaDotnetHost = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return viaDotnetHost
            ? $"\"{process}\" \"{typeof(DesktopIntegration).Assembly.Location}\""
            : $"\"{process}\"";
    }

    /// <summary>
    /// Resolves <see cref="PopupAnchor.Automatic"/>: on KDE Plasma, read which panel holds the system tray from
    /// <c>plasma-org.kde.plasma.desktop-appletsrc</c> (location 3 top, 4 bottom, 5 left, 6 right). Elsewhere, or when
    /// the file can't be read (Flatpak without that permission): top right.
    /// </summary>
    public static PopupAnchor Resolve(PopupAnchor anchor)
    {
        if (anchor != PopupAnchor.Automatic)
            return anchor;
        return DetectPlasmaTrayEdge() switch
        {
            4 => PopupAnchor.BottomRight,
            5 => PopupAnchor.BottomLeft,
            6 => PopupAnchor.BottomRight,
            _ => PopupAnchor.TopRight,
        };
    }

    private static int? DetectPlasmaTrayEdge()
    {
        string file = Path.Combine(Sandbox.HostConfigDirectory, "plasma-org.kde.plasma.desktop-appletsrc");
        if (!File.Exists(file))
            return null;
        try
        {
            // Containment id -> location, and the set of containments that host the systray applet.
            var locations = new Dictionary<string, int>();
            var trayHosts = new HashSet<string>();
            string? section = null;
            foreach (string line in File.ReadLines(file))
            {
                if (line.StartsWith('['))
                {
                    section = line;
                    continue;
                }
                if (section is null)
                    continue;
                var top = TopLevelContainment().Match(section);
                if (top.Success && line.StartsWith("location=") && int.TryParse(line[9..], out int loc))
                    locations[top.Groups[1].Value] = loc;
                var applet = AppletSection().Match(section);
                if (applet.Success && line.Trim() == "plugin=org.kde.plasma.systemtray")
                    trayHosts.Add(applet.Groups[1].Value);
            }
            foreach (string host in trayHosts)
                if (locations.TryGetValue(host, out int loc) && loc is >= 3 and <= 6)
                    return loc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    [GeneratedRegex(@"^\[Containments\]\[(\d+)\]$")]
    private static partial Regex TopLevelContainment();

    [GeneratedRegex(@"^\[Containments\]\[(\d+)\]\[Applets\]\[\d+\]$")]
    private static partial Regex AppletSection();
}
