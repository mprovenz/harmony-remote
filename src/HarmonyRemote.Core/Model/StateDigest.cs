using System.Text.Json;
using HarmonyRemote.Core.Protocol;

namespace HarmonyRemote.Core.Model;

/// <summary>Activity transition state from the digest (inferred from the Android <c>StateDigestProcessor</c>, confirmed live for 0/1/2).</summary>
public enum ActivityStatus
{
    Unknown = -1,
    /// <summary>No transition in progress. Whether anything is on comes from <see cref="StateDigest.ActivityId"/>.</summary>
    Settled = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
}

/// <summary>The hub's state digest (<c>connect.statedigest?get</c> and <c>connect.stateDigest?notify</c>).</summary>
public sealed record StateDigest(
    string ActivityId,
    ActivityStatus ActivityStatus,
    long StateVersion,
    long ConfigVersion,
    long ContentVersion,
    int SyncStatus,
    long SleepTimerId,
    string? HubSwVersion,
    IReadOnlyList<string> RunningActivities)
{
    public bool IsOff => ActivityId == Model.HubActivity.PowerOffId;

    public static StateDigest Parse(JsonElement d) => new(
        Str(d, "activityId") ?? Model.HubActivity.PowerOffId,
        int.TryParse(Str(d, "activityStatus"), out int status) && Enum.IsDefined(typeof(ActivityStatus), status)
            ? (ActivityStatus)status
            : ActivityStatus.Unknown,
        Long(d, "stateVersion"),
        Long(d, "configVersion"),
        Long(d, "contentVersion"),
        (int)Long(d, "syncStatus"),
        Long(d, "sleepTimerId", -1),
        Str(d, "hubSwVersion"),
        (Str(d, "runningActivityList") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) ? HubMessage.ScalarToString(el) : null;

    private static long Long(JsonElement obj, string name, long fallback = 0) =>
        long.TryParse(Str(obj, name), out long v) ? v : fallback;
}
