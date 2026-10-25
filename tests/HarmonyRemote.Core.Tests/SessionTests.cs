using System.Text.Json.Nodes;
using HarmonyRemote.Core.Discovery;
using HarmonyRemote.Core.Model;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.Core.Tests;

public class SessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<(FakeHub hub, HubSession session)> ConnectAsync(Action<FakeHub>? setup = null)
    {
        var hub = new FakeHub();
        setup?.Invoke(hub);
        await hub.StartAsync();
        var session = new HubSession(
            "127.0.0.1",
            (_, _) => Task.FromResult(new ProvisionInfo("42", "https://svcs.myharmony.com/Discovery/Discovery.svc", "1")),
            e => new HubConnection(e with { Port = hub.Port }));
        session.Start();
        await WaitUntil(() => session.IsConnected && session.State is not null && session.Config is not null);
        return (hub, session);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<JsonObject> HoldActions(FakeHub hub) =>
        hub.Received.Where(e => (string?)e["hbus"]!["cmd"] == HubCommands.HoldAction);

    [Fact]
    public async Task Connect_loads_identity_config_and_state()
    {
        var (hub, session) = await ConnectAsync();
        await using var _ = hub;
        await using var __ = session;

        Assert.Equal("Fake Hub", session.Identity!.Name);
        Assert.Equal("4.15.600", session.Identity.FirmwareVersion);
        Assert.Equal(2, session.Config!.OrderedActivities.Count());
        Assert.True(session.State!.IsOff);

        var first = hub.Received.First();
        Assert.Equal("42", (string?)first["hubId"]);
        Assert.NotNull(first["hbus"]!["id"]);
    }

    [Fact]
    public async Task Start_activity_waits_past_the_first_200_ack()
    {
        var (hub, session) = await ConnectAsync(h => h.ActivityDuration = TimeSpan.FromMilliseconds(600));
        await using var _ = hub;
        await using var __ = session;

        var progress = new List<ActivityProgress>();
        session.ActivityProgressChanged += (_, p) => progress.Add(p);
        var started = DateTime.UtcNow;
        await session.StartActivityAsync("200").WaitAsync(Wait);

        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(550), "returned on the ACK instead of completion");
        Assert.Equal("200", session.State!.ActivityId);
        Assert.Equal(ActivityStatus.Running, session.State.ActivityStatus);
        Assert.Null(session.PendingActivityId);
        Assert.Contains(progress, p => p is { Done: 1, Total: 2 });

        var sent = hub.Received.Single(e => (string?)e["hbus"]!["cmd"] == HubCommands.RunActivity)["hbus"]!["params"]!;
        Assert.Equal("start", (string?)sent["args"]!["rule"]);
        Assert.True((bool)sent["async"]!);
    }

    [Fact]
    public async Task Power_off_ends_the_running_activity()
    {
        var (hub, session) = await ConnectAsync(h => { h.ActivityId = "200"; h.ActivityStatus = 2; h.ActivityDuration = TimeSpan.FromMilliseconds(50); });
        await using var _ = hub;
        await using var __ = session;

        await session.PowerOffAsync().WaitAsync(Wait);

        var sent = hub.Received.Single(e => (string?)e["hbus"]!["cmd"] == HubCommands.RunActivity)["hbus"]!["params"]!;
        Assert.Equal("200", (string?)sent["activityId"]);
        Assert.Equal("end", (string?)sent["args"]!["rule"]);
        Assert.True(session.State!.IsOff);
    }

    [Fact]
    public async Task Tap_sends_press_then_release_with_one_id_and_no_hold()
    {
        var (hub, session) = await ConnectAsync();
        await using var _ = hub;
        await using var __ = session;
        var volumeDown = session.Config!.Activity("200")!.Function("VolumeDown")!;

        await session.TapAsync(volumeDown);
        await WaitUntil(() => HoldActions(hub).Count() >= 2);

        var sent = HoldActions(hub).ToList();
        Assert.Equal(["press", "release"], sent.Select(e => (string?)e["hbus"]!["params"]!["status"]));
        Assert.Single(sent.Select(e => (string?)e["hbus"]!["id"]).Distinct());
        Assert.Equal(volumeDown.Action, (string?)sent[0]["hbus"]!["params"]!["action"]);
    }

    [Fact]
    public async Task Held_button_sends_hold_heartbeats()
    {
        var (hub, session) = await ConnectAsync();
        await using var _ = hub;
        await using var __ = session;
        var button = session.Press(session.Config!.Activity("200")!.Function("VolumeUp")!);
        await Task.Delay(700);
        await button.ReleaseAsync();
        await WaitUntil(() => HoldActions(hub).Any(e => (string?)e["hbus"]!["params"]!["status"] == "release"));

        var states = HoldActions(hub).Select(e => (string?)e["hbus"]!["params"]!["status"]).ToList();
        Assert.Equal("press", states[0]);
        Assert.Equal("release", states[^1]);
        Assert.InRange(states.Count(s => s == "hold"), 2, 4); // ~every 200 ms over 700 ms
    }

    [Fact]
    public async Task Digest_event_updates_state_and_config_version_bump_reloads_config()
    {
        var (hub, session) = await ConnectAsync();
        await using var _ = hub;
        await using var __ = session;
        int configRequests() => hub.Received.Count(e => (string?)e["hbus"]!["cmd"] == HubCommands.GetConfig);
        Assert.Equal(1, configRequests());

        hub.ActivityId = "100"; hub.ActivityStatus = 2; hub.ConfigVersion = 11;
        await hub.PushEventAsync(HubEvents.StateDigestNotify, hub.Digest());

        await WaitUntil(() => session.State!.ActivityId == "100");
        Assert.Equal("Watch TV", session.CurrentActivity!.Label);
        await WaitUntil(() => configRequests() == 2);
    }

    [Fact]
    public async Task Reconnects_after_the_hub_drops_the_connection()
    {
        var (hub, session) = await ConnectAsync();
        await using var _ = hub;
        await using var __ = session;

        await hub.DropAsync();
        await WaitUntil(() => session.Status != SessionStatus.Connected);
        await WaitUntil(() => session.IsConnected);
        Assert.True(hub.Received.Count(e => (string?)e["hbus"]!["cmd"] == HubCommands.GetConfig) >= 2);
    }
}
