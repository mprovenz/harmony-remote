using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HarmonyRemote.App.Settings;
using HarmonyRemote.Core;
using HarmonyRemote.Core.Discovery;
using HarmonyRemote.Core.Model;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.App.ViewModels;

public sealed record AnchorOption(PopupAnchor Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Everything the popup and tray menu show. Mirrors a <see cref="HubSession"/> onto the UI thread.</summary>
public sealed partial class RemoteViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsStore _store;
    private readonly DispatcherTimer _messageTimer;
    private HubSession? _session;
    private HubConfig? _shownConfig;
    private string? _shownActivityId = "";
    private ActivityProgress? _lastProgress;
    private string? _flash;

    public RemoteViewModel(SettingsStore store)
    {
        _store = store;
        _hubAddressInput = store.Settings.HubHost ?? "";
        _selectedAnchor = AnchorOptions.First(o => o.Value == store.Settings.Anchor);
        _startAtLogin = DesktopIntegration.IsAutostartEnabled(store.Settings);
        _showDevices = store.Settings.ShowDevices;
        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            _flash = null;
            Sync();
        };
        Sync();
    }

    public IReadOnlyList<AnchorOption> AnchorOptions { get; } =
    [
        new(PopupAnchor.Automatic, "Next to the tray"),
        new(PopupAnchor.TopRight, "Top right"),
        new(PopupAnchor.BottomRight, "Bottom right"),
        new(PopupAnchor.TopLeft, "Top left"),
        new(PopupAnchor.BottomLeft, "Bottom left"),
    ];

    public ObservableCollection<ActivityItemViewModel> Activities { get; } = [];

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];

    public ObservableCollection<DiscoveredHub> DiscoveredHubs { get; } = [];

    public PopupAnchor ResolvedAnchor => DesktopIntegration.Resolve(_store.Settings.Anchor);

    /// <summary>Raised when the popup should move (anchor setting changed).</summary>
    public event Action? AnchorChanged;

    // ---- display ---------------------------------------------------------------------------------------------------

    [ObservableProperty] private string _hubName = "Harmony";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _displayTitle = "";
    [ObservableProperty] private string _displaySubtitle = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isOff = true;

    // ---- activities and devices --------------------------------------------------------------------------------------

    /// <summary>Keys of the running activity (null when everything is off).</summary>
    [ObservableProperty] private ControlPadViewModel? _activityPad;

    /// <summary>Whether a config is loaded, i.e. there is something to put under the Activities / Devices tabs.</summary>
    [ObservableProperty] private bool _hasConfig;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceList), nameof(ShowDevicePage))]
    private bool _showDevices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceList), nameof(ShowDevicePage))]
    private DeviceItemViewModel? _openDeviceItem;

    /// <summary>Keys of the device whose page is open.</summary>
    [ObservableProperty] private ControlPadViewModel? _devicePad;

    public bool ShowDeviceList => ShowDevices && OpenDeviceItem is null;

    public bool ShowDevicePage => ShowDevices && OpenDeviceItem is not null;

    // ---- settings ----------------------------------------------------------------------------------------------------

    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private string _hubAddressInput;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _searchMessage;
    [ObservableProperty] private AnchorOption _selectedAnchor;
    [ObservableProperty] private bool _startAtLogin;

    public bool HasHub => _store.Settings.HubHost is { Length: > 0 };

    public HubSession? Session => _session;

    public void Start()
    {
        if (HasHub)
            StartSession(_store.Settings.HubHost!);
        else
            ShowSettings = true;
    }

    // ---- session plumbing --------------------------------------------------------------------------------------------

    private void StartSession(string host)
    {
        var session = new HubSession(host);
        session.Changed += (_, _) => Dispatcher.UIThread.Post(Sync);
        session.ActivityProgressChanged += (_, p) => Dispatcher.UIThread.Post(() =>
        {
            _lastProgress = p;
            Sync();
        });
        _session = session;
        _shownConfig = null;
        _shownActivityId = "";
        session.Start();
        Sync();
    }

    private async Task RestartSessionAsync(string host)
    {
        var old = _session;
        _session = null;
        Sync();
        if (old is not null)
            await old.DisposeAsync();
        StartSession(host);
    }

    /// <summary>Copies session state into bindable properties. UI thread only.</summary>
    private void Sync()
    {
        var s = _session;
        if (s is null)
        {
            HubName = "Harmony";
            IsConnected = false;
            IsBusy = false;
            IsOff = true;
            DisplayTitle = "No hub yet";
            DisplaySubtitle = _flash ?? (ShowSettings
                ? "Enter its address below, or search this network."
                : "Open settings to add your hub.");
            if (Activities.Count > 0)
                Activities.Clear();
            RebuildDevices(null);
            ActivityPad = null;
            HasConfig = false;
            _shownActivityId = null;
            PowerOffCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasHub));
            return;
        }

        HubName = s.Identity?.DisplayName ?? s.Host;
        IsConnected = s.IsConnected;

        var config = s.Config;
        if (!ReferenceEquals(config, _shownConfig))
        {
            RebuildActivities(config);
            RebuildDevices(config);
            _shownConfig = config;
            _shownActivityId = "";
            HasConfig = config is not null;
        }

        var state = s.State;
        var current = s.CurrentActivity;
        string? pending = s.PendingActivityId;
        foreach (var a in Activities)
        {
            a.IsCurrent = current?.Id == a.Id;
            a.IsPending = pending == a.Id;
        }
        if (current?.Id != _shownActivityId)
        {
            ActivityPad = current is null ? null : new ControlPadViewModel(this, current, isDevice: false);
            _shownActivityId = current?.Id;
        }

        bool stopping = pending == HubActivity.PowerOffId || state?.ActivityStatus == ActivityStatus.Stopping;
        bool starting = !stopping && (pending is not null || state?.ActivityStatus == ActivityStatus.Starting);
        IsBusy = s.IsConnected && (starting || stopping);
        IsOff = state?.IsOff ?? true;
        if (!IsBusy)
            _lastProgress = null;
        string startingId = pending ?? state?.ActivityId ?? "";
        if (_lastProgress is { Total: > 0 } p && p.ActivityId == startingId)
        {
            IsProgressIndeterminate = false;
            Progress = 100.0 * p.Done / p.Total;
        }
        else
        {
            IsProgressIndeterminate = true;
        }

        (DisplayTitle, string subtitle) = (s.Status, s.IsConnected) switch
        {
            (SessionStatus.Connecting, _) when config is null => ("Connecting", $"Reaching the hub at {s.Host}"),
            (_, false) => (current?.Label ?? "Hub offline", $"Can't reach the hub at {s.Host}. Retrying."),
            _ when stopping => ("Turning off", "Switching everything off"),
            _ when starting => (config?.Activity(startingId)?.Label ?? "Starting",
                _lastProgress is { Total: > 0 } pr && pr.ActivityId == startingId
                    ? $"Starting, {pr.Done} of {pr.Total} devices ready"
                    : "Starting"),
            _ when current is not null => (current.Label, VolumeLine(config, current)),
            _ => ("Everything is off", "Pick an activity to start."),
        };
        DisplaySubtitle = _flash ?? subtitle;
        PowerOffCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasHub));
    }

    private static string VolumeLine(HubConfig? config, HubActivity activity) =>
        config?.Device(activity.VolumeDeviceId) is { } device ? $"Volume on {device.Label}" : "Running";

    private void RebuildActivities(HubConfig? config)
    {
        Activities.Clear();
        if (config is null)
            return;
        foreach (var a in config.OrderedActivities)
            Activities.Add(new ActivityItemViewModel(this, a.Id, a.Label));
    }

    private void RebuildDevices(HubConfig? config)
    {
        // Keep the open page across config reloads (and restore the saved one on start) when that device still exists.
        string? openId = OpenDeviceItem?.Id ?? _store.Settings.OpenDeviceId;
        Devices.Clear();
        if (config is not null)
            foreach (var device in config.OrderedDevices)
                Devices.Add(new DeviceItemViewModel(this, device));

        var item = Devices.FirstOrDefault(d => d.Id == openId);
        DevicePad = item is not null && config?.Device(item.Id) is { } open
            ? new ControlPadViewModel(this, open, isDevice: true)
            : null;
        OpenDeviceItem = DevicePad is null ? null : item;
    }

    internal void OpenDevice(DeviceItemViewModel item)
    {
        if (_session?.Config?.Device(item.Id) is not { } device)
            return;
        DevicePad = new ControlPadViewModel(this, device, isDevice: true);
        OpenDeviceItem = item;
        RememberView();
    }

    [RelayCommand]
    private void CloseDevice()
    {
        OpenDeviceItem = null;
        DevicePad = null;
        RememberView();
    }

    [RelayCommand]
    private void ShowActivitiesTab() => ShowDevices = false;

    [RelayCommand]
    private void ShowDevicesTab() => ShowDevices = true;

    partial void OnShowDevicesChanged(bool value) => RememberView();

    private void RememberView()
    {
        if (_store.Settings.ShowDevices == ShowDevices && _store.Settings.OpenDeviceId == OpenDeviceItem?.Id)
            return;
        _store.Settings.ShowDevices = ShowDevices;
        _store.Settings.OpenDeviceId = OpenDeviceItem?.Id;
        // Only worth a file once a hub is set up; before that there's nothing to come back to.
        if (HasHub)
            _store.Save();
    }

    private void Flash(string message)
    {
        _flash = message;
        _messageTimer.Stop();
        _messageTimer.Start();
        Sync();
    }

    // ---- actions -----------------------------------------------------------------------------------------------------

    internal HeldButton? TryPress(HubFunction function)
    {
        if (_session is not { IsConnected: true } s)
            return null;
        try
        {
            return s.Press(function);
        }
        catch (HubRequestException ex)
        {
            Flash($"Couldn't send {function.Label}: {ex.Code}");
            return null;
        }
    }

    internal async Task TapAsync(HubFunction function)
    {
        if (_session is not { IsConnected: true } s)
            return;
        try
        {
            await s.TapAsync(function);
        }
        catch (Exception ex) when (ex is HubRequestException or OperationCanceledException)
        {
            Flash($"Couldn't send {function.Label}");
        }
    }

    internal async Task StartActivityAsync(string id, string label)
    {
        if (_session is not { IsConnected: true } s)
            return;
        try
        {
            await s.StartActivityAsync(id);
        }
        catch (HubRequestException ex)
        {
            Flash($"Couldn't start {label} (hub said {ex.Code})");
        }
        catch (TimeoutException)
        {
            Flash($"{label} is taking longer than expected");
        }
    }

    private bool CanPowerOff() => _session is { IsConnected: true, State.IsOff: false };

    [RelayCommand(CanExecute = nameof(CanPowerOff))]
    private async Task PowerOffAsync()
    {
        if (_session is not { } s)
            return;
        try
        {
            await s.PowerOffAsync();
        }
        catch (HubRequestException ex)
        {
            Flash($"Couldn't turn everything off (hub said {ex.Code})");
        }
        catch (TimeoutException)
        {
            Flash("Turning off is taking longer than expected");
        }
    }

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        string host = HubAddressInput.Trim();
        if (host.Length == 0)
        {
            SearchMessage = "Enter the hub's IP address, for example 192.168.1.20.";
            return;
        }
        _store.Settings.HubHost = host;
        _store.Save();
        SearchMessage = null;
        ShowSettings = false;
        await RestartSessionAsync(host);
    }

    [RelayCommand]
    private async Task FindHubsAsync()
    {
        IsSearching = true;
        SearchMessage = "Looking for hubs…";
        DiscoveredHubs.Clear();
        try
        {
            var targets = HubDiscovery.BroadcastTargets().ToList();
            // Broadcast stays on this subnet; probing a typed or saved address directly also finds hubs behind a router.
            foreach (string candidate in new[] { HubAddressInput.Trim(), _store.Settings.HubHost ?? "" })
                if (IPAddress.TryParse(candidate, out var ip))
                    targets.Add(ip);
            var hubs = await HubDiscovery.DiscoverAsync(targets, TimeSpan.FromSeconds(7));
            foreach (var hub in hubs)
                DiscoveredHubs.Add(hub);
            SearchMessage = hubs.Count switch
            {
                0 => "No hubs answered. If your hub is on another network, enter its address above.",
                1 => "Found 1 hub. Select it to connect.",
                _ => $"Found {hubs.Count} hubs. Select one to connect.",
            };
        }
        catch (SocketException)
        {
            SearchMessage = "Port 5446 is in use, so hubs can't answer. Enter the hub's address above instead.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private Task UseHubAsync(DiscoveredHub hub)
    {
        HubAddressInput = hub.Ip;
        return ConnectAsync();
    }

    partial void OnShowSettingsChanged(bool value) => Sync();

    partial void OnSelectedAnchorChanged(AnchorOption value)
    {
        if (_store.Settings.Anchor == value.Value)
            return;
        _store.Settings.Anchor = value.Value;
        _store.Save();
        AnchorChanged?.Invoke();
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (!_revertingStartAtLogin && value != DesktopIntegration.IsAutostartEnabled(_store.Settings))
            _ = ApplyStartAtLoginAsync(value);
    }

    private bool _revertingStartAtLogin;

    private async Task ApplyStartAtLoginAsync(bool value)
    {
        try
        {
            await DesktopIntegration.SetAutostartAsync(value);
            _store.Settings.StartAtLogin = value;
            _store.Save();
        }
        catch (Exception ex)
        {
            // File, portal or D-Bus failures all land here: report and put the toggle back.
            Flash($"Couldn't change start at login: {ex.Message}");
            _revertingStartAtLogin = true;
            StartAtLogin = !value;
            _revertingStartAtLogin = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _messageTimer.Stop();
        if (_session is { } s)
            await s.DisposeAsync();
    }
}
