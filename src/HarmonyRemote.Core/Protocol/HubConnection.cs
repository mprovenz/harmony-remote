using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HarmonyRemote.Core.Protocol;

/// <summary>Where and how to open the hub's local WebSocket.</summary>
/// <param name="Host">Hub IP or host name.</param>
/// <param name="RemoteId">The hub's <c>remoteId</c> (from discovery or <c>getProvisionInfo</c>).</param>
/// <param name="Domain">Host part of <c>discoveryServerUri</c> without a <c>cf-</c> prefix, e.g. <c>svcs.myharmony.com</c>.</param>
/// <param name="Port">Always 8088 on current firmware.</param>
public sealed record HubEndpoint(string Host, string RemoteId, string Domain, int Port = 8088)
{
    public Uri WebSocketUri => new($"ws://{Host}:{Port}/?domain={Uri.EscapeDataString(Domain)}&hubId={Uri.EscapeDataString(RemoteId)}");
}

/// <summary>
/// One WebSocket connection to a hub: sends <c>hbus</c> requests, matches replies by id (the hub may answer under a
/// different <c>cmd</c>), and raises unsolicited events. No reconnect logic here; see <see cref="HubSession"/>.
/// </summary>
public sealed class HubConnection : IAsyncDisposable
{
    private readonly HubEndpoint _endpoint;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _sinceConnect = new();
    private readonly string _idPrefix = $"hr{Random.Shared.Next(1000)}";
    private long _nextId;
    private Task? _receiveLoop;
    private int _closed;

    public HubConnection(HubEndpoint endpoint)
    {
        _endpoint = endpoint;
        // The Android app pings every 45 s. With KeepAliveTimeout set, .NET sends real PING frames and aborts when no PONG arrives.
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(45);
        _socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>Unsolicited hub message (no request id). Raised on the receive thread.</summary>
    public event Action<HubMessage>? EventReceived;

    /// <summary>The connection ended (null = closed by us). Raised once.</summary>
    public event Action<Exception?>? Closed;

    public HubEndpoint Endpoint => _endpoint;

    public bool IsOpen => _socket.State == WebSocketState.Open && Volatile.Read(ref _closed) == 0;

    /// <summary>Milliseconds since the connection opened. The hub expects this as <c>timestamp</c> on async requests.</summary>
    public long Timestamp => _sinceConnect.ElapsedMilliseconds;

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _socket.ConnectAsync(_endpoint.WebSocketUri, timeout.Token).ConfigureAwait(false);
        _sinceConnect.Restart();
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public string NewRequestId() => $"{_idPrefix}-{Interlocked.Increment(ref _nextId)}";

    /// <summary>
    /// Sends a request and waits for its final reply. By default the first reply that is not progress
    /// (<c>100</c> / <c>200.2</c>) is final; pass <paramref name="isFinal"/> for commands that ACK first (runactivity).
    /// </summary>
    public async Task<HubMessage> RequestAsync(
        string command,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken ct = default,
        Func<HubMessage, bool>? isFinal = null,
        Action<HubMessage>? onProgress = null)
    {
        string id = NewRequestId();
        var pending = new PendingRequest(command, isFinal ?? (m => !m.IsProgress), onProgress);
        _pending[id] = pending;
        try
        {
            await SendAsync(command, parameters, id, ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(timeout);
            HubMessage reply;
            try
            {
                reply = await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HubRequestException(command, IsOpen ? "timeout" : "disconnected", null);
            }
            if (!reply.IsSuccess)
                throw new HubRequestException(command, reply.Code, reply.Message);
            return reply;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget send (button presses get no reply). Returns the request id used.</summary>
    public async Task<string> SendAsync(string command, JsonObject? parameters, string? requestId = null, CancellationToken ct = default)
    {
        string id = requestId ?? NewRequestId();
        var envelope = new JsonObject
        {
            ["hubId"] = _endpoint.RemoteId,
            ["timeout"] = 30,
            ["hbus"] = new JsonObject
            {
                ["cmd"] = command,
                ["id"] = id,
                ["params"] = parameters ?? new JsonObject(),
            },
        };
        byte[] bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
        return id;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        Exception? failure = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    failure = new WebSocketException($"hub closed the connection ({result.CloseStatus} {result.CloseStatusDescription})");
                    break;
                }
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Dispatch(json);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        OnClosed(failure);
    }

    private void Dispatch(string json)
    {
        HubMessage msg;
        try
        {
            msg = HubMessage.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (msg.IsEvent)
        {
            EventReceived?.Invoke(msg);
            return;
        }
        if (msg.Id is null || !_pending.TryGetValue(msg.Id, out var pending))
            return;

        if (pending.IsFinal(msg))
            pending.Completion.TrySetResult(msg);
        else
            pending.OnProgress?.Invoke(msg);
    }

    private void OnClosed(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetException(new HubRequestException(pending.Command, "disconnected", failure?.Message));
        Closed?.Invoke(failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Closing is best effort.
            }
        }
        _cts.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch (Exception) { }
        }
        OnClosed(null);
        _socket.Dispose();
        _cts.Dispose();
        _sendLock.Dispose();
    }

    private sealed class PendingRequest(string command, Func<HubMessage, bool> isFinal, Action<HubMessage>? onProgress)
    {
        public string Command { get; } = command;
        public Func<HubMessage, bool> IsFinal { get; } = isFinal;
        public Action<HubMessage>? OnProgress { get; } = onProgress;
        public TaskCompletionSource<HubMessage> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
