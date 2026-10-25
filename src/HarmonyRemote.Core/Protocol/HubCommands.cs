namespace HarmonyRemote.Core.Protocol;

/// <summary>
/// Command strings sent in <c>hbus.cmd</c>, as used by the official Android app (5.7.17) and seen on a real hub.
/// </summary>
public static class HubCommands
{
    public const string GetConfig = "vnd.logitech.harmony/vnd.logitech.harmony.engine?config";
    public const string GetStateDigest = "connect.statedigest?get";
    public const string GetSystemInfo = "vnd.logitech.harmony/vnd.logitech.harmony.system?systeminfo";
    public const string GetDiscoveryInfo = "connect.discoveryinfo?get";
    public const string RunActivity = "harmony.activityengine?runactivity";
    public const string HoldAction = "vnd.logitech.harmony/vnd.logitech.harmony.engine?holdAction";
    public const string ChangeChannel = "harmony.engine?changeChannel";

    /// <summary>Plain HTTP POST to <c>http://&lt;ip&gt;:8088</c>, not the WebSocket.</summary>
    public const string GetProvisionInfo = "setup.account?getProvisionInfo";
}

/// <summary>Unsolicited message types (<c>type</c> field of messages without an <c>id</c>).</summary>
public static class HubEvents
{
    /// <summary>The hub spells it with a capital D; the APK constant is lower-case. Compare case-insensitively.</summary>
    public const string StateDigestNotify = "connect.stateDigest?notify";
    public const string StartActivityFinished = "harmony.engine?startActivityFinished";
    public const string AutomationStateNotify = "automation.state?notify";
    public const string MetadataNotify = "harmonyengine.metadata?notify";

    public static bool Is(string? type, string expected) =>
        string.Equals(type, expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Values of <c>holdAction</c>'s <c>status</c> (Android <c>ICommand.CommandState</c>).</summary>
public enum ButtonState
{
    Press,
    Hold,
    Release,
    PressRelease,
}

public static class ButtonStateExtensions
{
    public static string ToWire(this ButtonState state) => state switch
    {
        ButtonState.Press => "press",
        ButtonState.Hold => "hold",
        ButtonState.Release => "release",
        ButtonState.PressRelease => "pressrelease",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
