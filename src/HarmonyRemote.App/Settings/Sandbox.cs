namespace HarmonyRemote.App.Settings;

/// <summary>
/// Flatpak awareness. Inside the sandbox <c>XDG_CONFIG_HOME</c> points at <c>~/.var/app/&lt;id&gt;/config</c>, host
/// files need explicit permissions, and start-at-login goes through the Background portal.
/// </summary>
public static class Sandbox
{
    public static string? FlatpakId { get; } =
        Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 } id ? id : null;

    public static bool IsFlatpak => FlatpakId is not null;

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The user's real config directory (where Plasma and autostart files live), also from inside Flatpak.</summary>
    public static string HostConfigDirectory =>
        !IsFlatpak && Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Home, ".config");

    /// <summary>
    /// A runtime directory every instance of the app can see. Inside Flatpak only
    /// <c>$XDG_RUNTIME_DIR/app/$FLATPAK_ID</c> is shared between instances.
    /// </summary>
    public static string SharedRuntimeDirectory
    {
        get
        {
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath();
            if (FlatpakId is { } id)
            {
                string appDir = Path.Combine(runtime, "app", id);
                Directory.CreateDirectory(appDir);
                return appDir;
            }
            return runtime;
        }
    }
}
