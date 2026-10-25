using System.Text.Json;

namespace HarmonyRemote.Core.Protocol;

/// <summary>
/// One message received from the hub's local WebSocket. Replies carry the request <see cref="Id"/>;
/// unsolicited events have no id and a <see cref="EventType"/> instead.
/// </summary>
public sealed record HubMessage(
    string? Id,
    string? Command,
    string Code,
    string? Message,
    JsonElement? Data,
    string? EventType)
{
    public bool IsEvent => Id is null;

    /// <summary><c>100</c> (continue) and <c>200.2</c> (async request accepted) are followed by more replies with the same id.</summary>
    public bool IsProgress => Code is "100" or "200.2";

    public bool IsSuccess => Code is "200" or "0";

    public static HubMessage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? id = root.TryGetProperty("id", out var idEl) ? ScalarToString(idEl) : null;
        string? cmd = root.TryGetProperty("cmd", out var cmdEl) ? ScalarToString(cmdEl) : null;
        string? type = root.TryGetProperty("type", out var typeEl) ? ScalarToString(typeEl) : null;
        string? msg = root.TryGetProperty("msg", out var msgEl) ? ScalarToString(msgEl) : null;
        // The Android client treats a missing code as 200 (WebSocketLocalTransport.processMessage).
        string code = root.TryGetProperty("code", out var codeEl) ? ScalarToString(codeEl) ?? "200" : "200";
        JsonElement? data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind != JsonValueKind.Null
            ? dataEl.Clone()
            : null;

        return new HubMessage(id, cmd, code, msg, data, id is null ? type : null);
    }

    /// <summary>
    /// The hub sends <c>code</c> as a string on some replies and as a JSON number on others (including the float
    /// <c>200.2</c>), so numbers are taken from their raw text rather than converted.
    /// </summary>
    internal static string? ScalarToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => el.GetRawText(),
    };
}

/// <summary>The request did not finish successfully (error code, timeout or a dropped connection).</summary>
public sealed class HubRequestException(string command, string code, string? message)
    : Exception($"{command} failed: {code} {message}")
{
    public string Command { get; } = command;
    public string Code { get; } = code;
    public string? HubMessage { get; } = message;
}
