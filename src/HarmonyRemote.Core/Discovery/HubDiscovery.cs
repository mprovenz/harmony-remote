using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HarmonyRemote.Core.Discovery;

/// <summary>
/// Harmony "reverse bonjour" discovery (Android <c>WiFiDiscovery</c>): listen on TCP 5446, send a UDP probe to port
/// 5224, and each hub connects back with a <c>key:value;</c> record. Broadcast only reaches the local subnet; a probe
/// sent to a hub's own IP works across routers.
/// </summary>
public static class HubDiscovery
{
    public const int ProbePort = 5224;
    public const int ConnectBackPort = 5446;

    /// <summary>
    /// Probes every <paramref name="targets"/> address (broadcast or unicast) every 3 s for <paramref name="duration"/>
    /// and returns the hubs that answered, de-duplicated by uuid / remoteId.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredHub>> DiscoverAsync(
        IEnumerable<IPAddress> targets,
        TimeSpan duration,
        CancellationToken ct = default)
    {
        var targetList = targets.Distinct().ToList();
        var found = new ConcurrentDictionary<string, DiscoveredHub>();

        var listener = new TcpListener(IPAddress.Any, ConnectBackPort);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);
        try
        {
            var accept = AcceptLoopAsync(listener, found, stop.Token);
            var probe = ProbeLoopAsync(targetList, stop.Token);
            await Task.WhenAll(accept, probe).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
        ct.ThrowIfCancellationRequested();
        return found.Values.OrderBy(h => h.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The limited broadcast address plus every up IPv4 interface's subnet broadcast.</summary>
    public static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.PrefixLength is <= 0 or >= 32)
                    continue;
                uint ip = ToUInt(addr.Address);
                uint mask = addr.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - addr.PrefixLength);
                yield return FromUInt(ip | ~mask);
            }
        }
    }

    /// <summary>The probe payload. The hub connects back to <paramref name="replyAddress"/>:5446.</summary>
    public static byte[] BuildProbe(IPAddress replyAddress) =>
        Encoding.ASCII.GetBytes($"_logitech-reverse-bonjour._tcp.local.\n{ConnectBackPort}\n{replyAddress}\nstring");

    /// <summary>The local address the OS would use to reach <paramref name="target"/> (no packet is sent).</summary>
    public static IPAddress LocalAddressFor(IPAddress target)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (target.Equals(IPAddress.Broadcast))
            s.EnableBroadcast = true;
        s.Connect(target, ProbePort);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }

    private static async Task ProbeLoopAsync(List<IPAddress> targets, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        while (!ct.IsCancellationRequested)
        {
            foreach (var target in targets)
            {
                try
                {
                    byte[] payload = BuildProbe(LocalAddressFor(target));
                    await udp.SendAsync(payload, new IPEndPoint(target, ProbePort), ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // Unreachable target or no route: keep probing the others.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, ConcurrentDictionary<string, DiscoveredHub> found, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        read.CancelAfter(TimeSpan.FromSeconds(3));
                        var stream = client.GetStream();
                        var buffer = new byte[4096];
                        using var ms = new MemoryStream();
                        int n;
                        while ((n = await stream.ReadAsync(buffer, read.Token).ConfigureAwait(false)) > 0)
                            ms.Write(buffer, 0, n);
                        string record = Encoding.UTF8.GetString(ms.ToArray());
                        string peer = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                        if (DiscoveredHub.ParseRecord(record, peer) is { } hub)
                            found[hub.Uuid ?? hub.RemoteId] = hub;
                    }
                    catch (Exception)
                    {
                        // A half-written record is dropped; the hub answers again on the next probe.
                    }
                }
            }, CancellationToken.None);
        }
    }

    private static uint ToUInt(IPAddress a)
    {
        byte[] b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint v) =>
        new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
}
