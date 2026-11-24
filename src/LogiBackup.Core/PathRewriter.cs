using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LogiBackup.Core;

/// <summary>
/// Rewrites the source user's profile folder (for example C:\Users\alice) into the target
/// user's folder. Matches whole path segments only, so C:\Users\bob never matches C:\Users\bobby.
/// </summary>
public sealed class PathRewriter
{
    private readonly (Regex pattern, string replacement)[] _rules;

    public bool IsNoOp { get; }

    public PathRewriter(string? sourceProfile, string? targetProfile)
    {
        sourceProfile = sourceProfile?.TrimEnd('\\', '/');
        targetProfile = targetProfile?.TrimEnd('\\', '/');
        IsNoOp = string.IsNullOrEmpty(sourceProfile) || string.IsNullOrEmpty(targetProfile) ||
                 string.Equals(sourceProfile, targetProfile, StringComparison.OrdinalIgnoreCase);
        if (IsNoOp) { _rules = []; return; }

        string backslash = sourceProfile!;
        string forward = sourceProfile!.Replace('\\', '/');
        string jsonEscaped = sourceProfile!.Replace("\\", "\\\\");
        _rules =
        [
            (Build(jsonEscaped), targetProfile!.Replace("\\", "\\\\")),
            (Build(backslash), targetProfile!),
            (Build(forward), targetProfile!.Replace('\\', '/')),
        ];
    }

    // A segment ends with a separator, a quote or the end of the string.
    private static Regex Build(string literal) =>
        new(Regex.Escape(literal) + @"(?=[\\/""]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Rewrites raw text (a JSON or YAML file on disk, escaped or not).</summary>
    public string RewriteText(string text)
    {
        foreach (var (pattern, replacement) in _rules)
            text = pattern.Replace(text, _ => replacement);
        return text;
    }

    /// <summary>Rewrites every string value of a JSON tree in place. Returns the number of values changed.</summary>
    public int RewriteJson(JsonNode? node)
    {
        if (IsNoOp || node is null) return 0;
        int changed = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue v && v.TryGetValue(out string? s))
                    {
                        string r = RewriteString(s);
                        if (r != s) { obj[key] = r; changed++; }
                    }
                    else changed += RewriteJson(obj[key]);
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue(out string? s))
                    {
                        string r = RewriteString(s);
                        if (r != s) { arr[i] = r; changed++; }
                    }
                    else changed += RewriteJson(arr[i]);
                }
                break;
        }
        return changed;
    }

    // Parsed JSON strings are already unescaped: skip the escaped rule.
    private string RewriteString(string s)
    {
        for (int i = 1; i < _rules.Length; i++)
            s = _rules[i].pattern.Replace(s, _ => _rules[i].replacement);
        return s;
    }
}
