using HarmonyRemote.Core.Model;

namespace HarmonyRemote.App.ViewModels;

/// <summary>
/// The keys of one activity or one device, sorted into the remote layout: rockers, d-pad, menu keys, playback, and (for
/// devices) power and inputs, plus every group for "More controls". Built once per source; replaced when it changes.
/// </summary>
public sealed class ControlPadViewModel
{
    private static readonly string[] NavOrder = ["Back", "Return", "Exit", "Home", "Menu", "Guide", "Info", "List"];
    private static readonly string[] TransportOrder = ["SkipBackward", "Rewind", "Play", "Pause", "Stop", "FastForward", "SkipForward", "Record"];

    private static readonly Dictionary<string, string> GroupTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NumericBasic"] = "Numbers",
        ["NavigationBasic"] = "Navigation",
        ["NavigationExtended"] = "More navigation",
        ["NavigationDVD"] = "Disc menu",
        ["NavigationDSTB"] = "Set-top box",
        ["TransportBasic"] = "Playback",
        ["TransportExtended"] = "Skip",
        ["TransportRecording"] = "Recording",
        ["Channel"] = "Channels",
        ["RadioTuner"] = "Tuner",
        ["PlayMode"] = "Play mode",
        ["PictureAdjustment"] = "Picture",
        ["SoundModes"] = "Sound",
        ["ColoredButtons"] = "Colour keys",
        ["GameType3"] = "Game",
        ["GoogleTVNavigation"] = "Apps",
        ["Miscellaneous"] = "Other",
    };

    public ControlPadViewModel(RemoteViewModel owner, IControlSource source, bool isDevice)
    {
        KeyViewModel? K(string name, string? shortLabel = null) =>
            source.Function(name) is { } f ? new KeyViewModel(owner, f, shortLabel) : null;

        VolumeUp = K("VolumeUp", "+");
        VolumeDown = K("VolumeDown", "−");
        Mute = K("Mute", "Mute");
        bool hasChannels = source.Function("ChannelUp") is not null;
        ChannelUp = hasChannels ? K("ChannelUp", "+") : K("NextPreset", "+");
        ChannelDown = hasChannels ? K("ChannelDown", "−") : K("PrevPreset", "−");
        PrevChannel = hasChannels ? K("PrevChannel", "Last") : null;
        ChannelCaption = hasChannels ? "Ch" : "Preset";
        Up = K("DirectionUp");
        Down = K("DirectionDown");
        Left = K("DirectionLeft");
        Right = K("DirectionRight");
        Select = K("Select", "OK");

        NavKeys = NavOrder.Select(n => K(n)).OfType<KeyViewModel>().DistinctBy(k => k.Function.Name).ToList();
        TransportKeys = TransportOrder.Select(n => K(n)).OfType<KeyViewModel>().ToList();
        PowerKeys = isDevice
            ? new[] { K("PowerOn", "On"), K("PowerOff", "Off"), K("PowerToggle", "Toggle") }.OfType<KeyViewModel>().ToList()
            : [];
        InputKeys = isDevice
            ? source.AllFunctions().Where(FunctionLabels.IsInput)
                .Select(f => new KeyViewModel(owner, f, FunctionLabels.InputKeyLabel(f))).ToList()
            : [];

        // On a device page the Power and Inputs rows already show those keys, so the full list leaves them out
        // (an input switch then has no extra list at all).
        var shownAbove = PowerKeys.Concat(InputKeys).Select(k => k.Function.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A config can repeat a group name (some TVs list PictureAdjustment twice): merge them.
        AllGroups = source.ControlGroups
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(same => new ControlGroupViewModel(
                GroupTitles.GetValueOrDefault(same.Key, FunctionLabels.Humanize(same.Key)),
                same.SelectMany(g => g.Functions)
                    .DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(f => !shownAbove.Contains(f.Name))
                    .Select(f => new KeyViewModel(owner, f)).ToList()))
            .Where(g => g.Keys.Count > 0)
            .ToList();
        AllGroupsTitle = isDevice ? "All controls" : "More controls";
        ExpandAllGroups = isDevice;
    }

    public KeyViewModel? VolumeUp { get; }
    public KeyViewModel? VolumeDown { get; }
    public KeyViewModel? Mute { get; }
    public KeyViewModel? ChannelUp { get; }
    public KeyViewModel? ChannelDown { get; }
    public KeyViewModel? PrevChannel { get; }
    public string ChannelCaption { get; }
    public KeyViewModel? Up { get; }
    public KeyViewModel? Down { get; }
    public KeyViewModel? Left { get; }
    public KeyViewModel? Right { get; }
    public KeyViewModel? Select { get; }

    public IReadOnlyList<KeyViewModel> NavKeys { get; }
    public IReadOnlyList<KeyViewModel> TransportKeys { get; }
    public IReadOnlyList<KeyViewModel> PowerKeys { get; }
    public IReadOnlyList<KeyViewModel> InputKeys { get; }
    public IReadOnlyList<ControlGroupViewModel> AllGroups { get; }

    /// <summary>"All controls" on a device page (opened, like the Harmony app's device screen); "More controls" for activities.</summary>
    public string AllGroupsTitle { get; }
    public bool ExpandAllGroups { get; }

    public bool HasVolume => VolumeUp is not null || VolumeDown is not null;
    public bool HasChannel => ChannelUp is not null || ChannelDown is not null;
    public bool HasDpad => Up is not null || Down is not null || Select is not null;
    /// <summary>Whether the rocker · d-pad · rocker block has anything in it (an input switch has none of it).</summary>
    public bool HasCluster => HasVolume || HasChannel || HasDpad;
    public bool HasNav => NavKeys.Count > 0;
    public bool HasTransport => TransportKeys.Count > 0;
    public bool HasPower => PowerKeys.Count > 0;
    public bool HasInputs => InputKeys.Count > 0;
    public bool HasGroups => AllGroups.Count > 0;
}
