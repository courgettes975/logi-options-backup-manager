using System.Text.RegularExpressions;

namespace LogiBackup.Core.Plugins;

/// <summary>
/// One entry of %LOCALAPPDATA%\Logi\LogiPluginService\Plugins.
/// Two kinds were observed on Plugin Service 6.4:
///  - an installed plugin: a folder named after the <c>name:</c> of metadata\LoupedeckPackage.yaml,
///    containing bin\ (or win\) and metadata\ ;
///  - a development link: a <c>&lt;Name&gt;.link</c> text file holding the absolute path of a build
///    folder that has the same bin\ + metadata\ layout.
/// </summary>
public sealed record PluginEntry(
    string EntryName,
    string? PackageName,
    string? Version,
    bool IsLink,
    string? LinkTarget,
    bool LinkTargetExists,
    long SizeBytes)
{
    /// <summary>Folder name to use when a link is installed as a real plugin folder.</summary>
    public string InstallFolderName => PackageName ?? Path.GetFileNameWithoutExtension(EntryName);
}

public static class PluginCatalog
{
    public const string MetadataFile = "metadata/LoupedeckPackage.yaml";
    private static readonly Regex NameLine = new(@"^name:\s*(\S.*?)\s*$", RegexOptions.Multiline);
    private static readonly Regex VersionLine = new(@"^version:\s*(\S.*?)\s*$", RegexOptions.Multiline);

    public static List<PluginEntry> Scan(string pluginsDir)
    {
        var list = new List<PluginEntry>();
        if (!Directory.Exists(pluginsDir)) return list;

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var (name, version) = ReadPackage(dir);
            list.Add(new PluginEntry(Path.GetFileName(dir), name, version, false, null, false, SizeOf(dir)));
        }
        foreach (var link in Directory.GetFiles(pluginsDir, "*.link"))
        {
            string target = ReadLinkTarget(link);
            bool exists = Directory.Exists(target);
            var (name, version) = exists ? ReadPackage(target) : (null, null);
            list.Add(new PluginEntry(Path.GetFileName(link), name, version, true, target, exists, exists ? SizeOf(target) : 0));
        }
        return list.OrderBy(p => p.EntryName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ReadLinkTarget(string linkFile) => File.ReadAllText(linkFile).Trim();

    /// <summary>Top-level name: and version: of LoupedeckPackage.yaml (indented keys are ignored).</summary>
    public static (string? name, string? version) ReadPackage(string pluginDir)
    {
        string yaml = Path.Combine(pluginDir, "metadata", "LoupedeckPackage.yaml");
        if (!File.Exists(yaml)) return (null, null);
        string text;
        using (var s = FileUtil.OpenShared(yaml)) using (var r = new StreamReader(s)) text = r.ReadToEnd();
        return ParsePackage(text);
    }

    public static (string? name, string? version) ParsePackage(string yaml)
    {
        string? Get(Regex re) => re.Match(yaml) is { Success: true } m ? m.Groups[1].Value.Trim('"', '\'') : null;
        return (Get(NameLine), Get(VersionLine));
    }

    private static long SizeOf(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
