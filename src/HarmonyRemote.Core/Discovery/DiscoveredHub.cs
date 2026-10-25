using System.Net;

namespace HarmonyRemote.Core.Discovery;

/// <summary>A hub as described by its discovery record (<c>key:value;</c> pairs) or by <c>connect.discoveryinfo?get</c>.</summary>
public sealed record DiscoveredHub(
    string Ip,
    string RemoteId,
    string? FriendlyName,
    string? FirmwareVersion,
    string? DiscoveryServerUri,
    string? Uuid,
    string? HubTypeId,
    string? ProductId,
    IReadOnlyDictionary<string, string> Fields)
{
    public string DisplayName => FriendlyName ?? Ip;

    /// <summary>The <c>domain</c> WebSocket parameter (Android <c>AppUtils.getDomainFromURL</c>).</summary>
    public string Domain => DomainFromUri(DiscoveryServerUri);

    /// <summary>Host of the discovery server URI with a leading <c>cf-</c> removed; falls back to the production default.</summary>
    public static string DomainFromUri(string? uri)
    {
        if (!string.IsNullOrEmpty(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            string host = parsed.Host;
            int cf = host.IndexOf("cf-", StringComparison.Ordinal);
            return cf >= 0 ? host[(cf + 3)..] : host;
        }
        return "svcs.myharmony.com";
    }

    /// <summary>Parses the record a hub writes to the discovery connect-back socket.</summary>
    public static DiscoveredHub? ParseRecord(string record, string? fallbackIp = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in record.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            // Values may contain ':' (URLs), so split on the first one only.
            if (colon < 0)
                fields[part] = "";
            else
                fields[part[..colon]] = part[(colon + 1)..];
        }
        return FromFields(fields, fallbackIp);
    }

    public static DiscoveredHub? FromFields(IReadOnlyDictionary<string, string> fields, string? fallbackIp = null)
    {
        string? ip = Get(fields, "ip") ?? fallbackIp;
        string? remoteId = Get(fields, "remoteId");
        if (ip is null || remoteId is null)
            return null;

        string? name = Get(fields, "friendlyName") ?? Get(fields, "host_name");
        if (name is not null)
            name = WebUtility.UrlDecode(name);

        return new DiscoveredHub(
            ip,
            remoteId,
            name,
            Get(fields, "current_fw_version"),
            Get(fields, "discoveryServerUri"),
            Get(fields, "uuid"),
            Get(fields, "hubId"),
            Get(fields, "productId"),
            fields);
    }

    private static string? Get(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? v) && !string.IsNullOrEmpty(v) ? v : null;
}
