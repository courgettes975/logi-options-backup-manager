using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LogiBackup.Core.Detection;
using LogiBackup.Core.Plugins;
using LogiBackup.Core.Processes;
using LogiBackup.Core.Settings;

namespace LogiBackup.Core.Backup;

public enum RestoreMode
{
    /// <summary>Only the portable keys are injected into the target's own settings. For another PC.</summary>
    Merge,
    /// <summary>The whole settings document is put back. Only for the same PC and Windows account (rollback).</summary>
    FullSameAccount,
}

public sealed class RestoreOptions
{
    public RestoreMode Mode { get; set; } = RestoreMode.Merge;
    public bool OptionsPlus { get; set; } = true;
    public bool CreativeConsole { get; set; } = true;
    public bool Media { get; set; } = true;
    public bool Registry { get; set; } = true;
    public bool Plugins { get; set; } = true;
    /// <summary>Off: plugins already installed on this PC are kept (they may be newer). On: replaced by the saved version.</summary>
    public bool ReplaceExistingPlugins { get; set; }
    /// <summary>Allows a restore when settings.db schema versions differ. Off by default.</summary>
    public bool ForceSchemaMismatch { get; set; }
}

public sealed class RestorePreview
{
    public BackupManifest Manifest { get; init; } = new();
    public List<string> Blockers { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool SchemaMismatch { get; set; }
    public bool SameAccount { get; set; }
    public MergeResult? Merge { get; set; }
    public List<string> MissingPlugins { get; } = [];
    public List<string> DevicesNotSeenOnTarget { get; } = [];
    public bool NeedsPathRewrite { get; set; }
    /// <summary>What happens to the archive's plugins when existing ones are kept.</summary>
    public List<PluginPlanItem> PluginPlanKeep { get; set; } = [];
    /// <summary>Same, when existing ones are replaced.</summary>
    public List<PluginPlanItem> PluginPlanReplace { get; set; } = [];

    public bool CanRestore(RestoreOptions o) =>
        Blockers.Count == 0 && (!SchemaMismatch || o.ForceSchemaMismatch || !o.OptionsPlus) &&
        (o.Mode != RestoreMode.FullSameAccount || SameAccount);
}

public sealed record RestoreResult(string SafetyBackupPath, MergeResult? Merge, int CreativeFiles, int MediaFiles, int RegistryValues, int Plugins = 0);

/// <summary>Checks a .logibak archive against this PC, then restores it.</summary>
public sealed class RestoreService(LogiPaths paths, IProgress<string>? log = null, IProcessControl? processes = null, string? safetyBackupDir = null)
{
    /// <summary>cc_config.json keys that describe the user's preferences (the rest is per-PC state).</summary>
    private static readonly string[] PortableCcConfigKeys =
        ["theme", "language", "lastActiveIconLibrary", "seenTours", "onboardingOpened", "pluginShortcutNotification"];

    public static string DefaultSafetyBackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogiOptionsBackupManager", "pre-restore");

    /// <summary>Read-only: verifies the archive and computes what a restore would change.</summary>
    public RestorePreview Analyze(string archivePath)
    {
        var manifest = BackupService.ReadManifest(archivePath);
        var p = new RestorePreview { Manifest = manifest };
        var target = EnvironmentDetector.Detect(paths);

        if (manifest.FormatVersion > BackupManifest.CurrentFormat)
            p.Blockers.Add($"Sauvegarde au format {manifest.FormatVersion}, plus récent que cet outil ({BackupManifest.CurrentFormat}).");

        foreach (var bad in VerifyIntegrity(archivePath, manifest))
            p.Blockers.Add($"Fichier corrompu ou modifié dans l'archive : {bad}");

        p.SameAccount = manifest.UserSidHash == target.UserSidHash &&
                        string.Equals(manifest.MachineName, target.MachineName, StringComparison.OrdinalIgnoreCase);
        p.NeedsPathRewrite = !new PathRewriter(manifest.UserProfilePath, paths.UserProfile).IsNoOp;
        if (p.NeedsPathRewrite)
            p.Warnings.Add($"Les chemins {manifest.UserProfilePath} seront réécrits en {paths.UserProfile}.");

        if (manifest.Sections.OptionsPlus)
        {
            if (target.OptionsPlus is null)
                p.Blockers.Add("Logi Options+ n'est pas installé sur ce PC. Installez-le, lancez-le une fois et appairez vos périphériques.");
            else if (!target.Settings.FormatOk)
                p.Blockers.Add($"settings.db de ce PC est absent ou illisible ({target.Settings.Error}). Lancez Logi Options+ une fois.");
            else
            {
                if (target.OptionsPlus.Version != manifest.OptionsPlusVersion)
                    p.Warnings.Add($"Version d'Options+ différente : sauvegarde {manifest.OptionsPlusVersion}, ce PC {target.OptionsPlus.Version}.");
                if (target.Settings.SchemaVersion != manifest.SettingsSchemaVersion)
                {
                    p.SchemaMismatch = true;
                    p.Warnings.Add($"Schéma de settings.db différent : sauvegarde {manifest.SettingsSchemaVersion}, ce PC {target.Settings.SchemaVersion}. " +
                                   "La restauration d'Options+ est bloquée sauf si vous la forcez.");
                }

                var portable = ReadJsonEntry(archivePath, ArchiveLayout.SettingsPortable);
                p.Merge = SettingsMerger.Merge(target.Settings.Json!, portable, new PathRewriter(manifest.UserProfilePath, paths.UserProfile));

                var known = SettingsMerger.KnownSlotPrefixes(target.Settings.Json!);
                p.DevicesNotSeenOnTarget.AddRange(manifest.SlotPrefixesInProfiles.Where(s => !known.Contains(s)));
                if (p.DevicesNotSeenOnTarget.Count > 0)
                    p.Warnings.Add("Périphériques jamais connectés à ce PC (leurs réglages s'appliqueront à leur première connexion) : " +
                                   string.Join(", ", p.DevicesNotSeenOnTarget));
            }
        }

        if (manifest.Sections.CreativeConsole)
        {
            if (target.PluginService is null)
                p.Warnings.Add("Logi Plugin Service n'est pas installé : les profils MX Creative Console seront copiés mais inutilisés.");
            else if (target.PluginService.Version != manifest.PluginServiceVersion)
                p.Warnings.Add($"Version du Plugin Service différente : sauvegarde {manifest.PluginServiceVersion}, ce PC {target.PluginService.Version}.");

            var installed = new HashSet<string>(target.Plugins, StringComparer.OrdinalIgnoreCase);
            var inArchive = new HashSet<string>(manifest.PluginPackages.Select(x => x.EntryName), StringComparer.OrdinalIgnoreCase);
            p.MissingPlugins.AddRange(manifest.Plugins.Where(pl => !installed.Contains(pl) && !inArchive.Contains(pl)));
            if (p.MissingPlugins.Count > 0)
                p.Warnings.Add("Plugins à réinstaller depuis le Marketplace : " + string.Join(", ", p.MissingPlugins));
        }

        if (manifest.Sections.Plugins)
        {
            using var zip = ZipFile.OpenRead(archivePath);
            p.PluginPlanKeep = PluginRestorer.Plan(manifest, zip, paths.PluginPlugins, replaceExisting: false);
            p.PluginPlanReplace = PluginRestorer.Plan(manifest, zip, paths.PluginPlugins, replaceExisting: true);
            foreach (var u in p.PluginPlanKeep.Where(i => i.Action == PluginAction.Unavailable))
                p.Warnings.Add($"Plugin {u.Source.EntryName} non restaurable : {u.Detail}");
        }
        return p;
    }

    public async Task<RestoreResult> RestoreAsync(string archivePath, RestoreOptions options, CancellationToken ct = default)
    {
        var preview = Analyze(archivePath);
        if (!preview.CanRestore(options))
            throw new InvalidOperationException("Restauration impossible :\n" + string.Join("\n", preview.Blockers.DefaultIfEmpty(
                options.Mode == RestoreMode.FullSameAccount && !preview.SameAccount
                    ? "La restauration complète n'est permise que sur le PC et le compte d'origine."
                    : "Schéma de settings.db différent (cochez « Forcer » pour continuer).")));
        var manifest = preview.Manifest;

        // 1. Safety copy of the current state, before anything is touched.
        string safetyDir = safetyBackupDir ?? DefaultSafetyBackupDir;
        Directory.CreateDirectory(safetyDir);
        string safety = Path.Combine(safetyDir, $"pre-restore-{DateTime.Now:yyyyMMdd-HHmmss}{ArchiveLayout.Extension}");
        log?.Report("Copie de sécurité de l'état actuel...");
        bool restorePlugins = options.Plugins && manifest.Sections.Plugins;
        await new BackupService(paths, log, processes).CreateAsync(safety,
            new BackupOptions { StopProcesses = false, Plugins = restorePlugins }, ct);

        // 2. Stop everything that writes these files.
        var controller = processes ?? new LogiProcessController(paths, log);
        var stopped = await controller.StopAllAsync(ct);

        MergeResult? merge = null;
        int creative = 0, media = 0, reg = 0, plugins = 0;
        var rewriter = new PathRewriter(manifest.UserProfilePath, paths.UserProfile);
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);

            if (options.OptionsPlus && manifest.Sections.OptionsPlus)
            {
                SettingsDbStore.Checkpoint(paths.SettingsDb);
                var current = SettingsDbStore.ReadJson(paths.SettingsDb);
                JsonObject result;
                if (options.Mode == RestoreMode.FullSameAccount)
                {
                    result = ReadJsonEntry(zip, ArchiveLayout.SettingsFull);
                    log?.Report("Options+ : restauration complète du document de réglages.");
                }
                else
                {
                    merge = SettingsMerger.Merge(current, ReadJsonEntry(zip, ArchiveLayout.SettingsPortable), rewriter);
                    result = merge.Json;
                    log?.Report($"Options+ : {merge.Replaced.Count} clé(s) remplacée(s), {merge.Added.Count} ajoutée(s), " +
                                $"{merge.Removed.Count} profil(s) retiré(s), {merge.RewrittenPaths} chemin(s) réécrit(s).");
                }
                SettingsDbStore.WriteJson(paths.SettingsDb, result);
                RestoreCcConfig(zip, options.Mode);
            }

            if (options.CreativeConsole && manifest.Sections.CreativeConsole)
            {
                creative = Extract(zip, ArchiveLayout.Applications, paths.PluginApplications, rewriter, ct);
                log?.Report($"MX Creative Console : {creative} fichier(s) de profils restauré(s).");
            }
            if (options.Media && manifest.Sections.Media)
            {
                media = Extract(zip, ArchiveLayout.Media, paths.PluginMedia, rewriter, ct);
                log?.Report($"Médias : {media} fichier(s).");
            }
            if (restorePlugins)
            {
                log?.Report("Plugins" + (options.ReplaceExistingPlugins ? " (remplacement des plugins existants)" : " (plugins existants conservés)") + " :");
                var plan = PluginRestorer.Plan(manifest, zip, paths.PluginPlugins, options.ReplaceExistingPlugins);
                plugins = PluginRestorer.Apply(plan, zip, paths.PluginPlugins, log, ct);
                log?.Report($"Plugins : {plugins} restauré(s), {plan.Count - plugins} non modifié(s).");
            }
            if (options.Registry && manifest.Sections.Registry && zip.GetEntry(ArchiveLayout.SharedSettings) is { } regEntry)
            {
                using var s = regEntry.Open();
                var values = JsonSerializer.Deserialize<List<RegistryValueDto>>(s, BackupManifest.Json) ?? [];
                reg = SharedSettingsRegistry.Import(values);
                log?.Report($"Registre : {reg} valeur(s).");
            }

            // 3. Read back what was written.
            if (options.OptionsPlus && manifest.Sections.OptionsPlus)
            {
                var check = SettingsDbStore.Inspect(paths.SettingsDb);
                if (!check.FormatOk) throw new InvalidOperationException("Vérification échouée : settings.db illisible après écriture. Restaurez la copie de sécurité : " + safety);
                log?.Report("Vérification de settings.db : OK.");
            }
        }
        catch (Exception ex)
        {
            log?.Report($"ERREUR : {ex.Message}");
            log?.Report($"La copie de sécurité est disponible : {safety}");
            throw;
        }
        finally
        {
            await controller.RestartAsync(stopped, CancellationToken.None);
        }
        return new RestoreResult(safety, merge, creative, media, reg, plugins);
    }

    private void RestoreCcConfig(ZipArchive zip, RestoreMode mode)
    {
        if (zip.GetEntry(ArchiveLayout.CcConfig) is not { } entry) return;
        JsonObject source;
        using (var s = entry.Open()) source = JsonNode.Parse(s) as JsonObject ?? new JsonObject();

        JsonObject result;
        if (mode == RestoreMode.FullSameAccount || !File.Exists(paths.CcConfig))
        {
            result = source;
            if (mode == RestoreMode.Merge) result.Remove("hostId"); // per-PC identifier, let Options+ create its own
        }
        else
        {
            string text;
            using (var s = FileUtil.OpenShared(paths.CcConfig)) using (var r = new StreamReader(s)) text = r.ReadToEnd();
            result = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            foreach (var key in PortableCcConfigKeys)
                if (source.ContainsKey(key)) result[key] = source[key]?.DeepClone();
        }
        File.WriteAllText(paths.CcConfig, result.ToJsonString(SettingsDbStore.WriteOptions), new UTF8Encoding(false));
        log?.Report("cc_config.json restauré.");
    }

    /// <summary>Extracts every entry under <paramref name="prefix"/> into <paramref name="destDir"/>, overwriting.</summary>
    private static int Extract(ZipArchive zip, string prefix, string destDir, PathRewriter rewriter, CancellationToken ct)
    {
        string root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
        int n = 0;
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            ct.ThrowIfCancellationRequested();
            string dest = Path.GetFullPath(Path.Combine(root, entry.FullName[prefix.Length..]));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Chemin refusé dans l'archive : {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (!rewriter.IsNoOp && FileUtil.IsRewritableText(dest))
            {
                using var s = entry.Open();
                using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                File.WriteAllText(dest, rewriter.RewriteText(r.ReadToEnd()), new UTF8Encoding(false));
            }
            else entry.ExtractToFile(dest, overwrite: true);
            n++;
        }
        return n;
    }

    private static List<string> VerifyIntegrity(string archivePath, BackupManifest manifest)
    {
        var bad = new List<string>();
        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var (name, hash) in manifest.Files)
        {
            var entry = zip.GetEntry(name);
            if (entry is null) { bad.Add(name + " (absent)"); continue; }
            using var s = entry.Open();
            if (!string.Equals(FileUtil.Sha256(s), hash, StringComparison.OrdinalIgnoreCase)) bad.Add(name);
        }
        return bad;
    }

    private static JsonObject ReadJsonEntry(string archivePath, string name)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return ReadJsonEntry(zip, name);
    }

    private static JsonObject ReadJsonEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} absent de l'archive.");
        using var s = entry.Open();
        return JsonNode.Parse(s) as JsonObject ?? throw new InvalidDataException($"{name} n'est pas un objet JSON.");
    }
}
