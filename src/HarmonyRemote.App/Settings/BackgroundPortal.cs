using Tmds.DBus.Protocol;

namespace HarmonyRemote.App.Settings;

/// <summary>
/// <c>org.freedesktop.portal.Background.RequestBackground</c>: the sandboxed way to start at login. The portal
/// writes (or removes) <c>~/.config/autostart/&lt;app-id&gt;.desktop</c> itself, with a <c>flatpak run</c> command line.
/// </summary>
internal static class BackgroundPortal
{
    private const string PortalBus = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";

    /// <summary>Returns true when the portal granted the request (autostart now matches <paramref name="autostart"/>).</summary>
    public static async Task<bool> RequestAutostartAsync(bool autostart, string[] commandLine, CancellationToken ct = default)
    {
        string address = DBusAddress.Session ?? throw new InvalidOperationException("No D-Bus session bus.");
        // An explicitly connected connection: UniqueName isn't available on the shared auto-connect one.
        using var connection = new DBusConnection(address);
        await connection.ConnectAsync();

        // The portal answers with a Response signal on a request object whose path we can predict from our
        // unique name and handle_token, so subscribe first to avoid missing a fast reply.
        string token = $"harmony_remote_{Random.Shared.Next(1_000_000)}";
        string sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var response = new TaskCompletionSource<(uint Code, bool Autostart)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Interface = "org.freedesktop.portal.Request",
                Member = "Response",
                Path = $"{PortalPath}/request/{sender}/{token}",
            },
            (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                uint code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (code, results.TryGetValue("autostart", out var granted) && granted.GetBool());
            },
            (Notification<(uint Code, bool Autostart)> n) =>
            {
                if (n.HasValue)
                    response.TrySetResult(n.Value);
            },
            emitOnCapturedContext: false);

        await connection.CallMethodAsync(CreateRequest(connection, token, autostart, commandLine));
        var (resultCode, autostartNow) = await response.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
        // 0 = success, 1 = the user cancelled, 2 = other error.
        return resultCode == 0 && autostartNow == autostart;
    }

    private static MessageBuffer CreateRequest(DBusConnection connection, string token, bool autostart, string[] commandLine)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(PortalBus, PortalPath, "org.freedesktop.portal.Background", "RequestBackground", "sa{sv}");
        writer.WriteString(""); // no parent window
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("reason");
        writer.WriteVariantString("Start Harmony Remote when you log in, so the remote is ready in the tray.");
        writer.WriteDictionaryEntryStart();
        writer.WriteString("autostart");
        writer.WriteVariantBool(autostart);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("commandline");
        writer.WriteSignature("as"); // a variant is its signature followed by the value
        writer.WriteArray(commandLine);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("dbus-activatable");
        writer.WriteVariantBool(false);
        writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }
}
