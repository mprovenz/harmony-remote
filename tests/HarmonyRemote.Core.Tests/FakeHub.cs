using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HarmonyRemote.Core.Tests;

/// <summary>
/// A loopback stand-in for the hub's WebSocket. Replies are modelled on traffic captured from a real hub
/// (including the double 200 for an activity start).
/// </summary>
internal sealed class FakeHub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<JsonObject> _received = new();
    private WebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string ConfigJson { get; set; } = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "config-small.json"));
    public string ActivityId { get; set; } = "-1";
    public int ActivityStatus { get; set; }
    public int ConfigVersion { get; set; } = 10;
    public int StateVersion { get; set; } = 1;

    /// <summary>How long the fake takes between the ACK and the real completion of runactivity.</summary>
    public TimeSpan ActivityDuration { get; set; } = TimeSpan.FromMilliseconds(400);

    public int Port { get; private set; }

    public IReadOnlyList<JsonObject> Received => _received.ToArray();

    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeHub()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.Map("/", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            _socket = await ctx.WebSockets.AcceptWebSocketAsync();
            Connected.TrySetResult();
            await ServeAsync(_socket, ctx.RequestAborted);
        });
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address).Port;
    }

    public JsonObject Digest() => new()
    {
        ["activityId"] = ActivityId,
        ["activityStatus"] = ActivityStatus,
        ["stateVersion"] = StateVersion,
        ["configVersion"] = ConfigVersion,
        ["contentVersion"] = 1,
        ["syncStatus"] = 0,
        ["sleepTimerId"] = -1,
        ["hubSwVersion"] = "4.15.600",
        ["runningActivityList"] = ActivityId == "-1" ? "" : ActivityId,
    };

    public Task PushEventAsync(string type, JsonObject data) =>
        SendAsync(new JsonObject { ["type"] = type, ["data"] = data });

    /// <summary>Simulates closing the connection from the hub side.</summary>
    public async Task DropAsync()
    {
        if (_socket is { State: WebSocketState.Open } s)
            await s.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "test drop", CancellationToken.None); // ServeAsync owns receiving
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct);
            }
            catch (Exception)
            {
                return;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            var envelope = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!.AsObject();
            _received.Enqueue(envelope);
            _ = Task.Run(() => RespondAsync(envelope["hbus"]!.AsObject()));
        }
    }

    private async Task RespondAsync(JsonObject hbus)
    {
        string cmd = (string)hbus["cmd"]!;
        string id = (string)hbus["id"]!;
        switch (cmd)
        {
            case Protocol.HubCommands.GetConfig:
                await ReplyAsync(cmd, id, "200", JsonNode.Parse(ConfigJson));
                break;
            case Protocol.HubCommands.GetStateDigest:
                await ReplyAsync(cmd, id, "200", Digest());
                break;
            case Protocol.HubCommands.GetDiscoveryInfo:
                await ReplyAsync(cmd, id, "200", new JsonObject
                {
                    ["friendlyName"] = "Fake%20Hub", ["current_fw_version"] = "4.15.600", ["remoteId"] = "42", ["ip"] = "127.0.0.1",
                });
                break;
            case Protocol.HubCommands.RunActivity:
                await RunActivityAsync(hbus, id);
                break;
            case Protocol.HubCommands.HoldAction:
                break; // the real hub never answers button presses
            default:
                await ReplyAsync(cmd, id, "404", null);
                break;
        }
    }

    private async Task RunActivityAsync(JsonObject hbus, string id)
    {
        var p = hbus["params"]!.AsObject();
        string target = (string)p["activityId"]!;
        string rule = (string)p["args"]!["rule"]!;
        string next = rule == "end" ? "-1" : target;

        // 1) immediate ACK: code 200 under the request's own cmd (NOT completion)
        await SendAsync(new JsonObject { ["cmd"] = Protocol.HubCommands.RunActivity, ["code"] = 200, ["id"] = id, ["msg"] = "OK" });
        // 2) accepted, numeric 200.2 under a different cmd
        await SendAsync(JsonNode.Parse($$$"""{"cmd":"harmony.engine?startActivity","code":200.2,"id":"{{{id}}}","msg":"Accepted","data":{"avSteps":true}}""")!.AsObject());
        ActivityId = next; ActivityStatus = rule == "end" ? 3 : 1; StateVersion++;
        await PushEventAsync(Protocol.HubEvents.StateDigestNotify, Digest());
        await SendAsync(JsonNode.Parse($$$"""{"cmd":"harmony.engine?startActivity","code":100,"id":"{{{id}}}","msg":"Continue","data":{"done":"1","total":"2","deviceId":"11"}}""")!.AsObject());
        await Task.Delay(ActivityDuration);
        // 3) the real completion
        await SendAsync(JsonNode.Parse($$$"""{"cmd":"harmony.engine?startActivity","code":200,"id":"{{{id}}}","msg":"OK","data":{"done":"2","total":"2","deviceId":"11"}}""")!.AsObject());
        ActivityStatus = rule == "end" ? 0 : 2; StateVersion++;
        await PushEventAsync(Protocol.HubEvents.StateDigestNotify, Digest());
        await PushEventAsync(Protocol.HubEvents.StartActivityFinished, new JsonObject { ["activityId"] = next, ["errorCode"] = "200", ["errorString"] = "OK" });
    }

    private Task ReplyAsync(string cmd, string id, string code, JsonNode? data)
    {
        var reply = new JsonObject { ["cmd"] = cmd, ["code"] = code, ["id"] = id, ["msg"] = code == "200" ? "OK" : "ERROR" };
        if (data is not null)
            reply["data"] = data;
        return SendAsync(reply);
    }

    private async Task SendAsync(JsonObject message)
    {
        if (_socket is not { State: WebSocketState.Open } s)
            return;
        await _sendLock.WaitAsync();
        try
        {
            await s.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
