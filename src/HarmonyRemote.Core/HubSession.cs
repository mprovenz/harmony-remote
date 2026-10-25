using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyRemote.Core.Discovery;
using HarmonyRemote.Core.Model;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.Core;

public enum SessionStatus
{
    Stopped,
    Connecting,
    Connected,
    Reconnecting,
}

/// <summary>What the session knows about the hub it is connected to.</summary>
public sealed record HubIdentity(string Host, string RemoteId, string Domain, string? Name, string? FirmwareVersion)
{
    public string DisplayName => Name ?? Host;
}

/// <summary>Per-device progress while an activity starts (<c>code 100</c> replies carry <c>done/total</c>).</summary>
public sealed record ActivityProgress(string ActivityId, int Done, int Total, string? DeviceId);

/// <summary>
/// A long-lived connection to one hub: bootstraps from an IP, keeps the WebSocket up (reconnects with backoff), loads the
/// config, tracks state from <c>stateDigest</c> events and exposes the control actions. Events are raised on background
/// threads; UI code must marshal.
/// </summary>
public sealed class HubSession : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan ActivityTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private readonly List<ActivityWaiter> _activityWaiters = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, CancellationToken, Task<ProvisionInfo>> _provision;
    private readonly Func<HubEndpoint, HubConnection> _connectionFactory;
    private HubConnection? _connection;
    private HubEndpoint? _endpoint;
    private Task? _runLoop;
    private long _loadedConfigVersion = -1;
    private int _configRefreshRunning;

    public HubSession(string host)
        : this(host, (h, ct) => HubProvisioning.GetProvisionInfoAsync(h, ct), e => new HubConnection(e))
    {
    }

    /// <summary>For tests: inject the provisioning call and the connection factory.</summary>
    internal HubSession(string host, Func<string, CancellationToken, Task<ProvisionInfo>> provision, Func<HubEndpoint, HubConnection> connectionFactory)
    {
        Host = host;
        _provision = provision;
        _connectionFactory = connectionFactory;
    }

    public string Host { get; }

    public SessionStatus Status { get; private set; } = SessionStatus.Stopped;

    public string? LastError { get; private set; }

    public HubIdentity? Identity { get; private set; }

    public HubConfig? Config { get; private set; }

    public StateDigest? State { get; private set; }

    /// <summary>The activity a start/stop request is in flight for (null when idle).</summary>
    public string? PendingActivityId { get; private set; }

    public bool IsConnected => Status == SessionStatus.Connected && _connection?.IsOpen == true;

    public HubActivity? CurrentActivity => State is { IsOff: false } s ? Config?.Activity(s.ActivityId) : null;

    /// <summary>Anything above changed.</summary>
    public event EventHandler? Changed;

    public event EventHandler<ActivityProgress>? ActivityProgressChanged;

    public void Start()
    {
        lock (_gate)
            _runLoop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    // ---- connection lifecycle -------------------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            SetStatus(attempt == 0 ? SessionStatus.Connecting : SessionStatus.Reconnecting);
            HubConnection? conn = null;
            try
            {
                _endpoint ??= await ResolveEndpointAsync(ct).ConfigureAwait(false);
                conn = _connectionFactory(_endpoint);
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                conn.Closed += ex => closed.TrySetResult(ex);
                conn.EventReceived += OnHubEvent;
                await conn.ConnectAsync(ct).ConfigureAwait(false);
                _connection = conn;

                await LoadIdentityAsync(conn, ct).ConfigureAwait(false);
                await LoadConfigAsync(conn, ct).ConfigureAwait(false);
                await LoadStateAsync(conn, ct).ConfigureAwait(false);

                attempt = 0;
                LastError = null;
                SetStatus(SessionStatus.Connected);

                var ended = await closed.Task.WaitAsync(ct).ConfigureAwait(false);
                LastError = ended?.Message ?? "connection closed";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                _connection = null;
                FailActivityWaiters("disconnected");
                if (conn is not null)
                    await conn.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
                break;
            SetStatus(SessionStatus.Reconnecting);
            try
            {
                await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        SetStatus(SessionStatus.Stopped);
    }

    private async Task<HubEndpoint> ResolveEndpointAsync(CancellationToken ct)
    {
        var info = await _provision(Host, ct).ConfigureAwait(false);
        Identity = new HubIdentity(Host, info.RemoteId, info.Domain, Identity?.Name, Identity?.FirmwareVersion);
        return new HubEndpoint(Host, info.RemoteId, info.Domain);
    }

    private async Task LoadIdentityAsync(HubConnection conn, CancellationToken ct)
    {
        try
        {
            var reply = await conn.RequestAsync(HubCommands.GetDiscoveryInfo, new JsonObject { ["format"] = "json" }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (reply.Data is { } data && DiscoveredHub.FromFields(ToFields(data), Host) is { } hub)
                Identity = new HubIdentity(Host, conn.Endpoint.RemoteId, conn.Endpoint.Domain, hub.FriendlyName, hub.FirmwareVersion);
        }
        catch (HubRequestException)
        {
            // The name is cosmetic; keep going without it.
            Identity ??= new HubIdentity(Host, conn.Endpoint.RemoteId, conn.Endpoint.Domain, null, null);
        }
    }

    private async Task LoadConfigAsync(HubConnection conn, CancellationToken ct)
    {
        var reply = await conn.RequestAsync(HubCommands.GetConfig, null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (reply.Data is not { } data)
            throw new HubRequestException(HubCommands.GetConfig, reply.Code, "config reply has no data");
        Config = HubConfig.Parse(data);
        _loadedConfigVersion = State?.ConfigVersion ?? -1;
        RaiseChanged();
    }

    private async Task LoadStateAsync(HubConnection conn, CancellationToken ct)
    {
        var reply = await conn.RequestAsync(HubCommands.GetStateDigest, new JsonObject { ["format"] = "json" }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (reply.Data is { } data)
            ApplyState(StateDigest.Parse(data), fromEvent: false);
    }

    // ---- events ---------------------------------------------------------------------------------------------------

    private void OnHubEvent(HubMessage msg)
    {
        if (HubEvents.Is(msg.EventType, HubEvents.StateDigestNotify) && msg.Data is { } digest)
        {
            ApplyState(StateDigest.Parse(digest), fromEvent: true);
        }
        else if (HubEvents.Is(msg.EventType, HubEvents.StartActivityFinished) && msg.Data is { } finished)
        {
            string? id = finished.TryGetProperty("activityId", out var el) ? HubMessage.ScalarToString(el) : null;
            CompleteActivityWaiters(w => w.MatchesFinished(id));
        }
    }

    private void ApplyState(StateDigest state, bool fromEvent)
    {
        State = state;
        if (_loadedConfigVersion < 0)
            _loadedConfigVersion = state.ConfigVersion;
        else if (fromEvent && state.ConfigVersion != _loadedConfigVersion)
            _ = RefreshConfigAsync();
        CompleteActivityWaiters(w => w.MatchesState(state));
        RaiseChanged();
    }

    /// <summary>Re-reads the config (the hub bumps <c>configVersion</c> after a sync from the cloud/app).</summary>
    public async Task RefreshConfigAsync()
    {
        if (_connection is not { } conn || Interlocked.Exchange(ref _configRefreshRunning, 1) == 1)
            return;
        try
        {
            await LoadConfigAsync(conn, _stop.Token).ConfigureAwait(false);
            if (State is { } s)
                _loadedConfigVersion = s.ConfigVersion;
        }
        catch (Exception ex) when (ex is HubRequestException or OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _configRefreshRunning, 0);
        }
    }

    // ---- actions --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts an activity and returns when the hub reports it running. The first <c>200</c> to <c>runactivity</c> is
    /// only an ACK; completion is the second <c>200</c> (sent as <c>harmony.engine?startActivity</c>), the
    /// <c>startActivityFinished</c> event, or a digest showing the activity running, whichever comes first.
    /// </summary>
    public Task StartActivityAsync(string activityId, CancellationToken ct = default)
    {
        if (activityId == HubActivity.PowerOffId)
            return PowerOffAsync(ct);
        if (State is { ActivityStatus: ActivityStatus.Running } s && s.ActivityId == activityId)
            return Task.CompletedTask;
        return RunActivityRuleAsync(activityId, "start", ActivityWaiter.ForStart(activityId), ct);
    }

    /// <summary>
    /// Turns everything off by ending the running activity (Android <c>stopCurrentActivity</c> on FW ≥ 4.0.0:
    /// <c>runactivity</c> with the running id and rule <c>end</c>). Not yet exercised against a real hub.
    /// </summary>
    public Task PowerOffAsync(CancellationToken ct = default)
    {
        if (State is not { } s || s.IsOff)
            return Task.CompletedTask;
        return RunActivityRuleAsync(s.ActivityId, "end", ActivityWaiter.ForPowerOff(), ct);
    }

    private async Task RunActivityRuleAsync(string activityId, string rule, ActivityWaiter waiter, CancellationToken ct)
    {
        var conn = _connection ?? throw new HubRequestException(HubCommands.RunActivity, "disconnected", "not connected");
        lock (_gate)
            _activityWaiters.Add(waiter);
        PendingActivityId = rule == "end" ? HubActivity.PowerOffId : activityId;
        RaiseChanged();
        try
        {
            var parameters = new JsonObject
            {
                ["activityId"] = activityId,
                ["timestamp"] = conn.Timestamp.ToString(),
                ["async"] = true,
                ["args"] = new JsonObject { ["rule"] = rule },
            };
            var reply = conn.RequestAsync(
                HubCommands.RunActivity,
                parameters,
                ActivityTimeout,
                ct,
                isFinal: m => !m.IsProgress && !(m.IsSuccess && m.Command == HubCommands.RunActivity),
                onProgress: m => ReportProgress(activityId, m));
            // If the state/event path wins, the request may still fail later (e.g. disconnect); observe that quietly.
            _ = reply.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            var first = await Task.WhenAny(reply, waiter.Completion.Task).WaitAsync(ActivityTimeout, ct).ConfigureAwait(false);
            await first.ConfigureAwait(false); // surfaces an error reply or a disconnect
        }
        finally
        {
            lock (_gate)
                _activityWaiters.Remove(waiter);
            PendingActivityId = null;
            RaiseChanged();
        }
    }

    private void ReportProgress(string activityId, HubMessage msg)
    {
        if (msg.Data is not { } d)
            return;
        int done = int.TryParse(d.TryGetProperty("done", out var a) ? HubMessage.ScalarToString(a) : null, out int x) ? x : 0;
        int total = int.TryParse(d.TryGetProperty("total", out var b) ? HubMessage.ScalarToString(b) : null, out int y) ? y : 0;
        string? device = d.TryGetProperty("deviceId", out var c) ? HubMessage.ScalarToString(c) : null;
        if (total > 0)
            ActivityProgressChanged?.Invoke(this, new ActivityProgress(activityId, done, total, device));
    }

    /// <summary>Starts holding a button: sends <c>press</c> now and <c>hold</c> every 200 ms until released.</summary>
    public HeldButton Press(HubFunction function)
    {
        var conn = _connection ?? throw new HubRequestException(HubCommands.HoldAction, "disconnected", "not connected");
        return HeldButton.Begin(conn, function.Action);
    }

    /// <summary>One click: <c>press</c>, 100 ms, <c>release</c> with one request id (verified live: one volume step each).</summary>
    public async Task TapAsync(HubFunction function, CancellationToken ct = default)
    {
        await using var button = Press(function);
        await Task.Delay(100, ct).ConfigureAwait(false);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private void CompleteActivityWaiters(Func<ActivityWaiter, bool> match)
    {
        lock (_gate)
        {
            foreach (var w in _activityWaiters.Where(match))
                w.Completion.TrySetResult();
        }
    }

    private void FailActivityWaiters(string reason)
    {
        lock (_gate)
        {
            foreach (var w in _activityWaiters)
                w.Completion.TrySetException(new HubRequestException(HubCommands.RunActivity, reason, null));
        }
    }

    private void SetStatus(SessionStatus status)
    {
        if (Status == status)
            return;
        Status = status;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static Dictionary<string, string> ToFields(JsonElement data)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data.ValueKind != JsonValueKind.Object)
            return fields;
        foreach (var p in data.EnumerateObject())
            if (HubMessage.ScalarToString(p.Value) is { } v)
                fields[p.Name] = v;
        return fields;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_runLoop is not null)
        {
            try { await _runLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        _stop.Dispose();
    }

    private sealed class ActivityWaiter
    {
        private readonly Func<StateDigest, bool> _state;
        private readonly Func<string?, bool> _finished;

        private ActivityWaiter(Func<StateDigest, bool> state, Func<string?, bool> finished)
        {
            _state = state;
            _finished = finished;
        }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool MatchesState(StateDigest s) => _state(s);

        public bool MatchesFinished(string? activityId) => _finished(activityId);

        public static ActivityWaiter ForStart(string id) =>
            new(s => s.ActivityId == id && s.ActivityStatus == ActivityStatus.Running, finished => finished == id);

        public static ActivityWaiter ForPowerOff() =>
            new(s => s.IsOff && s.ActivityStatus == ActivityStatus.Settled, finished => finished == HubActivity.PowerOffId);
    }
}

/// <summary>
/// A button being held. Mirrors the Android app: <c>press</c>, then <c>hold</c> every 200 ms (Android
/// <c>ActionHoldButton</c> heartbeat), then <c>release</c>, all under one request id. Dispose = release.
/// </summary>
public sealed class HeldButton : IAsyncDisposable
{
    private static readonly TimeSpan HoldInterval = TimeSpan.FromMilliseconds(200);
    private readonly HubConnection _conn;
    private readonly string _action;
    private readonly string _requestId;
    private readonly CancellationTokenSource _holding = new();
    private Task _press = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;
    private int _released;

    private HeldButton(HubConnection conn, string action)
    {
        _conn = conn;
        _action = action;
        _requestId = conn.NewRequestId();
    }

    internal static HeldButton Begin(HubConnection conn, string action)
    {
        var b = new HeldButton(conn, action);
        b._press = b.SendAsync(ButtonState.Press);
        b._heartbeat = b.HeartbeatAsync();
        return b;
    }

    public async ValueTask ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;
        _holding.Cancel();
        try { await _heartbeat.ConfigureAwait(false); } catch (Exception) { }
        try { await _press.ConfigureAwait(false); } catch (Exception) { }
        try { await SendAsync(ButtonState.Release).ConfigureAwait(false); } catch (Exception) { }
        _holding.Dispose();
    }

    public ValueTask DisposeAsync() => ReleaseAsync();

    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(HoldInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_holding.Token).ConfigureAwait(false))
                await SendAsync(ButtonState.Hold).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task SendAsync(ButtonState state) =>
        _conn.SendAsync(HubCommands.HoldAction, new JsonObject
        {
            ["action"] = _action,
            ["status"] = state.ToWire(),
            ["timestamp"] = _conn.Timestamp,
        }, _requestId);
}
