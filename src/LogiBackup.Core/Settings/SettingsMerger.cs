using System.Text.Json.Nodes;

namespace LogiBackup.Core.Settings;

/// <summary>
/// Builds the portable part of a settings.db JSON and merges it into the JSON of another PC.
/// </summary>
public static class SettingsMerger
{
    /// <summary>Per-run fields of the <c>applications</c> entries that only make sense on the source PC.</summary>
    private static readonly string[] VolatileApplicationFields = ["processId", "sessionId", "commandLine"];

    /// <summary>Keeps only the Portable keys, cleaned. This is what goes into settings.portable.json.</summary>
    public static JsonObject ExtractPortable(JsonObject full)
    {
        var result = new JsonObject();
        foreach (var (key, value) in full)
        {
            if (SettingsClassifier.Classify(key).Category != KeyCategory.Portable) continue;
            var copy = value?.DeepClone();
            if (key == "applications") CleanApplications(copy);
            result[key] = copy;
        }
        return result;
    }

    private static void CleanApplications(JsonNode? applicationsNode)
    {
        if (applicationsNode?["applications"] is not JsonArray list) return;
        foreach (var app in list.OfType<JsonObject>())
            foreach (var field in VolatileApplicationFields)
                app.Remove(field);
    }

    /// <summary>
    /// Returns a copy of <paramref name="target"/> where every Portable key comes from
    /// <paramref name="portable"/>. Profiles are replaced as a whole set (the target's own
    /// <c>profile-*</c> entries are removed) so that <c>profile_keys</c> stays consistent.
    /// Every other key of the target (serials, pairing, account, unknown keys) is left untouched.
    /// </summary>
    public static MergeResult Merge(JsonObject target, JsonObject portable, PathRewriter rewriter)
    {
        var result = (JsonObject)target.DeepClone();
        var report = new MergeResult { Json = result };

        bool sourceHasProfiles = portable.Any(p => SettingsClassifier.IsProfileKey(p.Key));
        if (sourceHasProfiles)
        {
            foreach (var key in result.Select(p => p.Key).Where(SettingsClassifier.IsProfileKey).ToList())
            {
                if (!portable.ContainsKey(key)) { result.Remove(key); report.Removed.Add(key); }
            }
        }

        foreach (var (key, value) in portable)
        {
            var cls = SettingsClassifier.Classify(key);
            if (cls.Category != KeyCategory.Portable)
            {
                report.Skipped.Add(cls); // a tampered or newer backup: never trust a non-portable key
                continue;
            }
            var copy = value?.DeepClone();
            report.RewrittenPaths += rewriter.RewriteJson(copy);
            if (result.ContainsKey(key)) report.Replaced.Add(key); else report.Added.Add(key);
            result[key] = copy;
        }
        return report;
    }

    /// <summary>Device models referenced by the assignments (slotId = "&lt;slotPrefix&gt;_&lt;control&gt;").</summary>
    public static SortedSet<string> SlotPrefixesUsedByProfiles(JsonObject json)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in json)
        {
            if (!SettingsClassifier.IsProfileKey(key) || value?["assignments"] is not JsonArray list) continue;
            foreach (var a in list.OfType<JsonObject>())
            {
                if (a["slotId"]?.GetValue<string>() is not { Length: > 0 } slot) continue;
                int i = slot.IndexOf('_');
                set.Add(i < 0 ? slot : slot[..i]);
            }
        }
        return set;
    }

    /// <summary>Device models known by an installation (ever seen on that PC).</summary>
    public static SortedSet<string> KnownSlotPrefixes(JsonObject json)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json["slot_prefixes_ever_seen"] is JsonArray arr)
            foreach (var v in arr) if (v?.GetValue<string>() is { } s) set.Add(s);
        return set;
    }

    /// <summary>Connected devices, without serial numbers, with the name from migration_report when present.</summary>
    public static List<DeviceInfo> Devices(JsonObject json)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json["migration_report"]?["devices"] is JsonArray rep)
            foreach (var d in rep.OfType<JsonObject>())
                if (d["modelId"]?.GetValue<string>() is { } m && d["deviceName"]?.GetValue<string>() is { } n) names[m] = n;

        var list = new List<DeviceInfo>();
        if (json["ever_connected_devices"]?["devices"] is JsonArray devs)
        {
            foreach (var d in devs.OfType<JsonObject>())
            {
                string model = d["deviceModel"]?.GetValue<string>() ?? "";
                list.Add(new DeviceInfo(
                    model,
                    names.GetValueOrDefault(model, model),
                    d["deviceType"]?.GetValue<string>() ?? "",
                    d["slotPrefix"]?.GetValue<string>() ?? "",
                    d["connectionType"]?.GetValue<string>() ?? ""));
            }
        }
        return list;
    }
}

public sealed record DeviceInfo(string ModelId, string Name, string DeviceType, string SlotPrefix, string ConnectionType);

public sealed class MergeResult
{
    public JsonObject Json { get; init; } = new();
    public List<string> Added { get; } = [];
    public List<string> Replaced { get; } = [];
    public List<string> Removed { get; } = [];
    public List<KeyClassification> Skipped { get; } = [];
    public int RewrittenPaths { get; set; }
}
