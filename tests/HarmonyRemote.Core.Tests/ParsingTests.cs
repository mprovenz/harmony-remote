using System.Net;
using System.Text;
using System.Text.Json;
using HarmonyRemote.Core.Discovery;
using HarmonyRemote.Core.Model;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.Core.Tests;

public class ParsingTests
{
    [Fact]
    public void Reply_code_is_taken_from_raw_number_text()
    {
        var msg = HubMessage.Parse("""{"cmd":"harmony.engine?startActivity","code":200.2,"id":"x","msg":"Accepted","data":{"avSteps":true}}""");
        Assert.Equal("200.2", msg.Code);
        Assert.True(msg.IsProgress);
        Assert.False(msg.IsEvent);
        Assert.Equal("harmony.engine?startActivity", msg.Command);
    }

    [Fact]
    public void Missing_code_defaults_to_200_and_string_codes_pass_through()
    {
        Assert.Equal("200", HubMessage.Parse("""{"id":"a","msg":"OK"}""").Code);
        Assert.Equal("583", HubMessage.Parse("""{"id":"a","code":"583","msg":"Failed"}""").Code);
    }

    [Fact]
    public void Message_without_id_is_an_event()
    {
        var msg = HubMessage.Parse("""{"type":"connect.stateDigest?notify","data":{"activityId":"5","activityStatus":2}}""");
        Assert.True(msg.IsEvent);
        Assert.True(HubEvents.Is(msg.EventType, HubEvents.StateDigestNotify));
        Assert.True(HubEvents.Is("connect.statedigest?notify", HubEvents.StateDigestNotify)); // APK spelling
    }

    [Fact]
    public void Discovery_record_splits_on_first_colon_and_decodes_name()
    {
        const string record = "ip:192.168.1.20;remoteId:12345678;friendlyName:Living%20Room;current_fw_version:4.15.600;" +
                              "discoveryServerUri:https://svcs.myharmony.com/Discovery/Discovery.svc;uuid:abc;hubId:106;setupSessionSetupType:;";
        var hub = DiscoveredHub.ParseRecord(record)!;
        Assert.Equal("192.168.1.20", hub.Ip);
        Assert.Equal("12345678", hub.RemoteId);
        Assert.Equal("Living Room", hub.FriendlyName);
        Assert.Equal("https://svcs.myharmony.com/Discovery/Discovery.svc", hub.DiscoveryServerUri);
        Assert.Equal("svcs.myharmony.com", hub.Domain);
        Assert.Equal("", hub.Fields["setupSessionSetupType"]);
    }

    [Theory]
    [InlineData("https://svcs.myharmony.com/Discovery/Discovery.svc", "svcs.myharmony.com")]
    [InlineData("https://cf-svcs.myharmony.com/Discovery/Discovery.svc", "svcs.myharmony.com")]
    [InlineData(null, "svcs.myharmony.com")]
    public void Domain_matches_android_getDomainFromURL(string? uri, string expected) =>
        Assert.Equal(expected, DiscoveredHub.DomainFromUri(uri));

    [Fact]
    public void Probe_payload_matches_android_format()
    {
        string payload = Encoding.ASCII.GetString(HubDiscovery.BuildProbe(IPAddress.Parse("192.168.1.50")));
        Assert.Equal("_logitech-reverse-bonjour._tcp.local.\n5446\n192.168.1.50\nstring", payload);
    }

    [Fact]
    public void Config_orders_activities_and_resolves_volume_role()
    {
        var config = HubConfig.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "config-small.json")));

        Assert.Equal(["Watch TV", "Listen to Music"], config.OrderedActivities.Select(a => a.Label));
        Assert.True(config.Activity("-1")!.IsPowerOff);

        var music = config.Activity("200")!;
        Assert.Equal("11", music.VolumeDeviceId);
        Assert.Equal("{\"command\":\"VolumeDown\",\"type\":\"IRCommand\",\"deviceId\":\"11\"}", music.Group("Volume")!.Find("VolumeDown")!.Action);
        Assert.NotNull(music.Function("Play"));

        Assert.Equal("12", config.Activity("100")!.VolumeDeviceId); // from "roles"
        Assert.Equal(33, config.Device("13")!.Transport);          // string "33" tolerated
    }

    [Fact]
    public void State_digest_parses_live_shape()
    {
        using var doc = JsonDocument.Parse("""
            {"sleepTimerId":-1,"activityId":"12345","activityStatus":2,"stateVersion":155,"configVersion":124,
             "contentVersion":93,"syncStatus":0,"hubSwVersion":"4.15.600","runningActivityList":"12345"}
            """);
        var s = StateDigest.Parse(doc.RootElement);
        Assert.Equal("12345", s.ActivityId);
        Assert.Equal(ActivityStatus.Running, s.ActivityStatus);
        Assert.False(s.IsOff);
        Assert.Equal(124, s.ConfigVersion);
        Assert.Equal(["12345"], s.RunningActivities);
    }
}
