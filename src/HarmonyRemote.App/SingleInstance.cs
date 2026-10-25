using System.Net.Sockets;
using System.Text;

namespace HarmonyRemote.App;

/// <summary>
/// One tray icon per user session. The first instance listens on a Unix socket in <c>$XDG_RUNTIME_DIR</c>; a later
/// launch sends "show" there (so starting the app again opens the remote) and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    private static string SocketPath =>
        Path.Combine(Settings.Sandbox.SharedRuntimeDirectory, $"harmony-remote-{Environment.UserName}.sock");

    /// <summary>Returns the guard for the first instance, or null after telling the running one to show itself.</summary>
    public static SingleInstance? TryAcquire()
    {
        string path = SocketPath;
        if (TrySignalRunning(path))
            return null;

        // Nobody answered: a stale socket file from a crash can be replaced.
        File.Delete(path);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(4);
        return new SingleInstance(listener, path);
    }

    /// <summary>Calls <paramref name="onShow"/> (on a background thread) whenever another launch asks to show.</summary>
    public void Listen(Action onShow) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                var buffer = new byte[16];
                int n = await client.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
                if (Encoding.ASCII.GetString(buffer, 0, n) == "show")
                    onShow();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
            }
        }
    });

    private static bool TrySignalRunning(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send(Encoding.ASCII.GetBytes("show"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
