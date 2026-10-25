using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.Core.Discovery;

/// <summary>Result of <c>setup.account?getProvisionInfo</c>: enough to open the WebSocket from a bare IP.</summary>
public sealed record ProvisionInfo(string RemoteId, string? DiscoveryServer, string? AccountId)
{
    public string Domain => DiscoveredHub.DomainFromUri(DiscoveryServer);
}

/// <summary>
/// Bootstraps a hub from its IP with one HTTP POST to <c>http://&lt;ip&gt;:8088</c>. Needs no inbound port, so it works
/// where discovery's connect-back is blocked (Android <c>HarmonyWebServices.getProvisionInfo</c>).
/// </summary>
public static class HubProvisioning
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<ProvisionInfo> GetProvisionInfoAsync(string host, CancellationToken ct = default, int port = 8088)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{host}:{port}/")
        {
            // A fixed-length body: the hub's HTTP server answers a chunked (JsonContent) POST with 417.
            Content = new StringContent(new JsonObject
            {
                ["id"] = "124",
                ["cmd"] = HubCommands.GetProvisionInfo,
                ["timeout"] = 90000,
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.ExpectContinue = false;
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Origin", "http://sl.dhg.myharmony.com");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var msg = HubMessage.Parse(json);
        if (!msg.IsSuccess || msg.Data is not { } data)
            throw new HubRequestException(HubCommands.GetProvisionInfo, msg.Code, msg.Message);

        string? remoteId = Str(data, "activeRemoteId") ?? Str(data, "remoteId");
        if (remoteId is null)
            throw new HubRequestException(HubCommands.GetProvisionInfo, msg.Code, "reply has no activeRemoteId");
        return new ProvisionInfo(remoteId, Str(data, "discoveryServer"), Str(data, "accountId"));
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) ? HubMessage.ScalarToString(el) : null;
}
