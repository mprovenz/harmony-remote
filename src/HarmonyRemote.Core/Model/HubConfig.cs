using System.Text.Json;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.Core.Model;

/// <summary>One button: <see cref="Action"/> is a JSON string that is sent back verbatim in <c>holdAction</c>.</summary>
public sealed record HubFunction(string Name, string Label, string Action);

public sealed record ControlGroup(string Name, IReadOnlyList<HubFunction> Functions)
{
    public HubFunction? Find(string functionName) =>
        Functions.FirstOrDefault(f => string.Equals(f.Name, functionName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Anything with keys: an activity (keys routed to its devices) or a single device.</summary>
public interface IControlSource
{
    string Id { get; }

    string Label { get; }

    IReadOnlyList<ControlGroup> ControlGroups { get; }
}

public static class ControlSourceExtensions
{
    public static ControlGroup? Group(this IControlSource source, string name) =>
        source.ControlGroups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>First function with this name in any group (names are unique enough within one activity or device).</summary>
    public static HubFunction? Function(this IControlSource source, string name) =>
        source.ControlGroups.Select(g => g.Find(name)).FirstOrDefault(f => f is not null);

    /// <summary>Every function in group order, first occurrence of each name only (a config can repeat a group).</summary>
    public static IEnumerable<HubFunction> AllFunctions(this IControlSource source) =>
        source.ControlGroups.SelectMany(g => g.Functions).DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
}

public sealed record HubActivity(
    string Id,
    string Label,
    int? Order,
    string? Type,
    string? VolumeDeviceId,
    IReadOnlyList<ControlGroup> ControlGroups) : IControlSource
{
    public const string PowerOffId = "-1";

    public bool IsPowerOff => Id == PowerOffId;
}

public sealed record HubDevice(
    string Id,
    string Label,
    string? Manufacturer,
    string? Model,
    string? Type,
    int? Transport,
    IReadOnlyList<ControlGroup> ControlGroups) : IControlSource
{
    /// <summary>"Sony KD-55X80K": the manufacturer and model, whichever are known.</summary>
    public string Description => string.Join(' ', new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record HubSequence(string Id, string Name);

/// <summary>The <c>data</c> of <c>vnd.logitech.harmony.engine?config</c>.</summary>
public sealed record HubConfig(
    IReadOnlyList<HubActivity> Activities,
    IReadOnlyList<HubDevice> Devices,
    IReadOnlyList<HubSequence> Sequences)
{
    /// <summary>Real activities in the hub's order (PowerOff excluded).</summary>
    public IEnumerable<HubActivity> OrderedActivities =>
        Activities.Where(a => !a.IsPowerOff).OrderBy(a => a.Order ?? int.MaxValue).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>Devices by name, as the device list shows them.</summary>
    public IEnumerable<HubDevice> OrderedDevices =>
        Devices.OrderBy(d => d.Label, StringComparer.OrdinalIgnoreCase);

    public HubActivity? Activity(string? id) => id is null ? null : Activities.FirstOrDefault(a => a.Id == id);

    public HubDevice? Device(string? id) => id is null ? null : Devices.FirstOrDefault(d => d.Id == id);

    public static HubConfig Parse(JsonElement data)
    {
        var activities = Array(data, "activity").Select(ParseActivity).ToList();
        var devices = Array(data, "device").Select(ParseDevice).ToList();
        var sequences = Array(data, "sequence")
            .Select(s => new HubSequence(Str(s, "id") ?? "", Str(s, "name") ?? Str(s, "label") ?? ""))
            .Where(s => s.Id.Length > 0)
            .ToList();
        return new HubConfig(activities, devices, sequences);
    }

    public static HubConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Accept either the bare config or a full reply envelope with "data".
        return Parse(root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root);
    }

    private static HubActivity ParseActivity(JsonElement a)
    {
        string? volume = Str(a, "VolumeActivityRole");
        if (volume is null && a.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Object)
            volume = Str(roles, "VolumeActivityRole");
        return new HubActivity(
            Str(a, "id") ?? "",
            Str(a, "label") ?? "",
            int.TryParse(Str(a, "activityOrder"), out int order) ? order : null,
            Str(a, "type"),
            volume,
            ParseGroups(a));
    }

    private static HubDevice ParseDevice(JsonElement d) => new(
        Str(d, "id") ?? "",
        Str(d, "label") ?? "",
        Str(d, "manufacturer"),
        Str(d, "model"),
        Str(d, "type"),
        int.TryParse(Str(d, "Transport"), out int transport) ? transport : null,
        ParseGroups(d));

    private static List<ControlGroup> ParseGroups(JsonElement owner) =>
        Array(owner, "controlGroup")
            .Select(g => new ControlGroup(
                Str(g, "name") ?? "",
                Array(g, "function")
                    .Select(f => new HubFunction(Str(f, "name") ?? "", Str(f, "label") ?? Str(f, "name") ?? "", Str(f, "action") ?? ""))
                    .Where(f => f.Action.Length > 0)
                    .ToList()))
            .ToList();

    private static IEnumerable<JsonElement> Array(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
            : [];

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) ? HubMessage.ScalarToString(el) : null;
}
