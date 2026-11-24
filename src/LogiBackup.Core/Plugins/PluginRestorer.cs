using System.IO.Compression;
using LogiBackup.Core.Backup;

namespace LogiBackup.Core.Plugins;

public enum PluginAction
{
    /// <summary>Not on this PC: its folder is extracted.</summary>
    Install,
    /// <summary>Already on this PC and "replace" is on: the folder is deleted, then extracted.</summary>
    Replace,
    /// <summary>Already on this PC: left as is.</summary>
    Skip,
    /// <summary>Development link whose build folder exists on this PC: the .link file is written.</summary>
    RestoreLink,
    /// <summary>Development link whose build folder is missing here: the saved build is installed as a normal plugin folder.</summary>
    InstallLinkedAsFolder,
    /// <summary>Nothing usable in the archive.</summary>
    Unavailable,
}

public sealed record PluginPlanItem(PluginEntry Source, PluginAction Action, string Detail);

public static class PluginRestorer
{
    /// <summary>Decides what happens to every plugin of the archive. Read-only.</summary>
    public static List<PluginPlanItem> Plan(BackupManifest manifest, ZipArchive zip, string pluginsDir, bool replaceExisting)
    {
        var installed = PluginCatalog.Scan(pluginsDir)
            .ToDictionary(p => p.IsLink ? p.EntryName : p.EntryName, StringComparer.OrdinalIgnoreCase);
        var plan = new List<PluginPlanItem>();

        foreach (var src in manifest.PluginPackages)
        {
            if (!src.IsLink)
            {
                if (installed.TryGetValue(src.EntryName, out var here))
                {
                    string versions = $"ici {here.Version ?? "?"}, sauvegarde {src.Version ?? "?"}";
                    plan.Add(replaceExisting
                        ? new(src, PluginAction.Replace, versions)
                        : new(src, PluginAction.Skip, "déjà installé (" + versions + ")"));
                }
                else plan.Add(new(src, PluginAction.Install, src.Version ?? ""));
                continue;
            }

            // Development link.
            if (src.LinkTarget is { } target && Directory.Exists(target))
            {
                bool present = installed.ContainsKey(src.EntryName);
                plan.Add(present && !replaceExisting
                    ? new(src, PluginAction.Skip, "lien déjà présent")
                    : new(src, PluginAction.RestoreLink, target));
                continue;
            }

            string prefix = $"{ArchiveLayout.PluginsLinked}{Path.GetFileNameWithoutExtension(src.EntryName)}/";
            if (!zip.Entries.Any(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                plan.Add(new(src, PluginAction.Unavailable, $"dossier {src.LinkTarget} absent ici et non sauvegardé"));
                continue;
            }
            string folder = src.InstallFolderName;
            if (installed.ContainsKey(folder) && !replaceExisting)
                plan.Add(new(src, PluginAction.Skip, $"dossier {folder} déjà installé"));
            else
                plan.Add(new(src, PluginAction.InstallLinkedAsFolder, $"installé dans Plugins\\{folder} ({src.LinkTarget} absent ici)"));
        }
        return plan;
    }

    /// <summary>Applies a plan. The Plugin Service must be stopped (its DLLs are loaded).</summary>
    public static int Apply(IEnumerable<PluginPlanItem> plan, ZipArchive zip, string pluginsDir, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(pluginsDir);
        int done = 0;
        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            var src = item.Source;
            switch (item.Action)
            {
                case PluginAction.Install:
                case PluginAction.Replace:
                    ReplaceFolder(zip, $"{ArchiveLayout.PluginsInstalled}{src.EntryName}/", Path.Combine(pluginsDir, src.EntryName), ct);
                    break;
                case PluginAction.RestoreLink:
                    var entry = zip.GetEntry(ArchiveLayout.PluginsLinks + src.EntryName)
                                ?? throw new InvalidDataException($"{src.EntryName} absent de l'archive.");
                    entry.ExtractToFile(SafeChild(pluginsDir, src.EntryName), overwrite: true);
                    break;
                case PluginAction.InstallLinkedAsFolder:
                    ReplaceFolder(zip, $"{ArchiveLayout.PluginsLinked}{Path.GetFileNameWithoutExtension(src.EntryName)}/",
                        Path.Combine(pluginsDir, src.InstallFolderName), ct);
                    break;
                default:
                    log?.Report($"  {src.EntryName} : ignoré, {item.Detail}");
                    continue;
            }
            log?.Report($"  {src.EntryName} : {Describe(item.Action)} {item.Detail}");
            done++;
        }
        return done;
    }

    public static string Describe(PluginAction a) => a switch
    {
        PluginAction.Install => "installé",
        PluginAction.Replace => "remplacé",
        PluginAction.Skip => "conservé",
        PluginAction.RestoreLink => "lien restauré vers",
        PluginAction.InstallLinkedAsFolder => "build de développement",
        _ => "indisponible",
    };

    private static void ReplaceFolder(ZipArchive zip, string prefix, string dest, CancellationToken ct)
    {
        string root = SafeChild(Path.GetDirectoryName(dest)!, Path.GetFileName(dest));
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true); // no stale DLL from another version
        root = root.TrimEnd('\\') + "\\";
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(root, e.FullName[prefix.Length..]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Chemin refusé dans l'archive : {e.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            e.ExtractToFile(path, overwrite: true);
        }
    }

    /// <summary>Refuses names that would escape the Plugins folder.</summary>
    private static string SafeChild(string parent, string name)
    {
        string root = Path.GetFullPath(parent).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length)
            throw new InvalidDataException($"Nom de plugin refusé : {name}");
        return full;
    }
}
