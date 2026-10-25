namespace HarmonyRemote.Core.Tests;

/// <summary>
/// Read-only checks against a real hub. Runs only when <c>HARMONY_LIVE_HUB</c> is set to the hub's IP address;
/// otherwise each test returns immediately. Never add control actions here.
/// </summary>
public class LiveHubTests
{
    private static string? LiveHost => Environment.GetEnvironmentVariable("HARMONY_LIVE_HUB");

    [Fact]
    public async Task Live_hub_connects_and_loads_config_and_state()
    {
        if (LiveHost is not { Length: > 0 } host)
            return;

        await using var session = new HubSession(host);
        session.Start();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(session.IsConnected && session.Config is not null && session.State is not null))
        {
            Assert.True(DateTime.UtcNow < deadline, $"not connected: {session.Status} {session.LastError}");
            await Task.Delay(100);
        }

        Assert.NotEmpty(session.Config.OrderedActivities);
        Assert.NotNull(session.Identity?.FirmwareVersion);
        Console.WriteLine($"LIVE {session.Identity!.DisplayName} fw={session.Identity.FirmwareVersion} " +
                          $"activities={session.Config.OrderedActivities.Count()} devices={session.Config.Devices.Count} " +
                          $"current={session.CurrentActivity?.Label ?? "off"} status={session.State.ActivityStatus}");
    }

    [Fact]
    public async Task Live_hub_answers_a_unicast_discovery_probe()
    {
        if (LiveHost is not { Length: > 0 } host)
            return;

        var hubs = await Discovery.HubDiscovery.DiscoverAsync([System.Net.IPAddress.Parse(host)], TimeSpan.FromSeconds(4));

        var hub = Assert.Single(hubs);
        Assert.Equal(host, hub.Ip);
        Assert.False(string.IsNullOrEmpty(hub.RemoteId));
        Console.WriteLine($"LIVE discovery: {hub.DisplayName} remoteId={hub.RemoteId} fw={hub.FirmwareVersion} domain={hub.Domain}");
    }
}
