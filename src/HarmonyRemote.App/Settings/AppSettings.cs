using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarmonyRemote.App.Settings;

/// <summary>Which screen corner the popup opens in. Linux trays don't report where the tray icon is.</summary>
public enum PopupAnchor
{
    Automatic,
    TopRight,
    BottomRight,
    TopLeft,
    BottomLeft,
}

public sealed class AppSettings
{
    /// <summary>Hub IP or host name. Null until the user picks one.</summary>
    public string? HubHost { get; set; }

    public PopupAnchor Anchor { get; set; } = PopupAnchor.Automatic;

    /// <summary>Only used inside Flatpak, where the autostart file isn't visible (see <see cref="DesktopIntegration"/>).</summary>
    public bool StartAtLogin { get; set; }

    /// <summary>The remote reopens where it was left: the Devices tab, and which device page was open.</summary>
    public bool ShowDevices { get; set; }

    public string? OpenDeviceId { get; set; }
}

/// <summary>Loads and saves <c>$XDG_CONFIG_HOME/harmony-remote/settings.json</c>.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private SettingsStore(string path, AppSettings settings)
    {
        Path = path;
        Settings = settings;
    }

    public string Path { get; }

    public AppSettings Settings { get; }

    public static string ConfigDirectory =>
        System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "harmony-remote");

    public static SettingsStore Load()
    {
        string path = System.IO.Path.Combine(ConfigDirectory, "settings.json");
        AppSettings settings = new();
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken file falls back to defaults; the next save rewrites it.
        }
        return new SettingsStore(path, settings);
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Json));
        File.Move(tmp, Path, overwrite: true);
    }
}
