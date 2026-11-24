using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LogiBackup.Core.Detection;
using LogiBackup.Core.Plugins;
using LogiBackup.Core.Processes;
using LogiBackup.Core.Settings;

namespace LogiBackup.Core.Backup;

public sealed class BackupOptions
{
    public bool OptionsPlus { get; set; } = true;
    public bool CreativeConsole { get; set; } = true;
    public bool Media { get; set; } = true;
    public bool Registry { get; set; } = true;
    /// <summary>Plugin files (installed folders and the builds behind development .link files).</summary>
    public bool Plugins { get; set; } = true;
    /// <summary>
    /// Not required: settings.db is read through SQLite, which gives a consistent snapshot even
    /// while Options+ writes. Stopping only protects the Creative Console JSON files from a write in progress.
    /// </summary>
    public bool StopProcesses { get; set; }
}

public sealed record BackupResult(string ArchivePath, BackupManifest Manifest, long SizeBytes);

/// <summary>Writes a .logibak archive (zip) with the portable settings and a manifest.</summary>
public sealed class BackupService(LogiPaths paths, IProgress<string>? log = null, IProcessControl? processes = null)
{
    public static string ToolVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";

    public async Task<BackupResult> CreateAsync(string archivePath, BackupOptions options, CancellationToken ct = default)
    {
        var report = EnvironmentDetector.Detect(paths);
        if (options.OptionsPlus && !report.Settings.FormatOk)
            throw new InvalidOperationException($"settings.db n'est pas exploitable : {report.Settings.Error}");

        var controller = processes ?? new LogiProcessController(paths, log);
        StopState? stopped = null;
        if (options.StopProcesses) stopped = await controller.StopAllAsync(ct);

        string temp = archivePath + ".tmp";
        try
        {
            var manifest = new BackupManifest
            {
                ToolVersion = ToolVersion,
                CreatedAt = DateTimeOffset.Now,
                MachineName = report.MachineName,
                UserName = report.UserName,
                UserSidHash = report.UserSidHash,
                UserProfilePath = paths.UserProfile,
                OptionsPlusVersion = report.OptionsPlus?.Version,
                PluginServiceVersion = report.PluginService?.Version,
            };

            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (options.OptionsPlus) WriteOptionsPlus(zip, manifest);
                if (options.CreativeConsole) WriteCreativeConsole(zip, manifest, ct);
                if (options.Media && Directory.Exists(paths.PluginMedia))
                {
                    log?.Report("Copie des médias (icônes, sons)...");
                    AddDirectory(zip, manifest, paths.PluginMedia, ArchiveLayout.Media, ct);
                    manifest.Sections.Media = true;
                }
                if (options.Plugins) WritePlugins(zip, manifest, ct);
                if (options.Registry)
                {
                    var values = SharedSettingsRegistry.Export();
                    AddText(zip, manifest, ArchiveLayout.SharedSettings, JsonSerializer.Serialize(values, BackupManifest.Json));
                    manifest.Sections.Registry = true;
                    log?.Report($"Registre : {values.Count} valeur(s) de {SharedSettingsRegistry.SubKey}");
                }

                // Written last: it lists the hashes of everything above.
                var entry = zip.CreateEntry(BackupManifest.FileName, CompressionLevel.Optimal);
                await using var es = entry.Open();
                await JsonSerializer.SerializeAsync(es, manifest, BackupManifest.Json, ct);
            }

            File.Move(temp, archivePath, overwrite: true);
            log?.Report($"Sauvegarde écrite : {archivePath}");
            return new BackupResult(archivePath, ReadManifest(archivePath), new FileInfo(archivePath).Length);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            if (stopped is not null) await controller.RestartAsync(stopped, CancellationToken.None);
        }
    }

    private void WriteOptionsPlus(ZipArchive zip, BackupManifest manifest)
    {
        log?.Report("Lecture de settings.db...");
        var full = SettingsDbStore.ReadJson(paths.SettingsDb);
        var portable = SettingsMerger.ExtractPortable(full);

        manifest.SettingsSchemaVersion = full["schema_version"]?.GetValue<int>();
        manifest.Devices = SettingsMerger.Devices(full);
        // Options+ ships preset assignments for models never connected (observed: mx-mechanical-*):
        // keep only the models this PC has really seen.
        var seen = SettingsMerger.KnownSlotPrefixes(full);
        manifest.SlotPrefixesInProfiles = SettingsMerger.SlotPrefixesUsedByProfiles(full)
            .Where(p => seen.Count == 0 || seen.Contains(p)).ToList();

        // The full copy is only used to roll back on the same PC and account.
        AddText(zip, manifest, ArchiveLayout.SettingsFull, full.ToJsonString(SettingsDbStore.WriteOptions));
        AddText(zip, manifest, ArchiveLayout.SettingsPortable, portable.ToJsonString(SettingsDbStore.WriteOptions));
        log?.Report($"Options+ : {portable.Count} clé(s) portable(s) sur {full.Count}.");

        if (File.Exists(paths.CcConfig))
        {
            using var s = FileUtil.OpenShared(paths.CcConfig);
            AddStream(zip, manifest, ArchiveLayout.CcConfig, s);
        }
        manifest.Sections.OptionsPlus = true;
    }

    private void WriteCreativeConsole(ZipArchive zip, BackupManifest manifest, CancellationToken ct)
    {
        if (!Directory.Exists(paths.PluginApplications))
        {
            log?.Report("Aucun profil MX Creative Console trouvé.");
            return;
        }
        log?.Report("Copie des profils MX Creative Console...");
        AddDirectory(zip, manifest, paths.PluginApplications, ArchiveLayout.Applications, ct);
        manifest.CreativeConsoleDeviceTypes = EnvironmentDetector.ScanCreativeConsole(paths.PluginApplications).Select(f => f.DeviceType).ToList();
        manifest.Plugins = EnvironmentDetector.ListPlugins(paths.PluginPlugins);
        AddText(zip, manifest, ArchiveLayout.PluginList, JsonSerializer.Serialize(manifest.Plugins, BackupManifest.Json));
        manifest.Sections.CreativeConsole = true;
    }

    private void WritePlugins(ZipArchive zip, BackupManifest manifest, CancellationToken ct)
    {
        var plugins = PluginCatalog.Scan(paths.PluginPlugins);
        if (plugins.Count == 0) { log?.Report("Aucun plugin trouvé."); return; }
        log?.Report($"Copie de {plugins.Count} plugin(s) ({plugins.Sum(p => p.SizeBytes) / 1048576.0:N0} Mo)...");
        foreach (var p in plugins)
        {
            ct.ThrowIfCancellationRequested();
            if (!p.IsLink)
            {
                AddDirectory(zip, manifest, Path.Combine(paths.PluginPlugins, p.EntryName), $"{ArchiveLayout.PluginsInstalled}{p.EntryName}/", ct);
            }
            else
            {
                using (var s = FileUtil.OpenShared(Path.Combine(paths.PluginPlugins, p.EntryName)))
                    AddStream(zip, manifest, ArchiveLayout.PluginsLinks + p.EntryName, s);
                if (p.LinkTargetExists)
                    AddDirectory(zip, manifest, p.LinkTarget!, $"{ArchiveLayout.PluginsLinked}{Path.GetFileNameWithoutExtension(p.EntryName)}/", ct);
                else
                    log?.Report($"  {p.EntryName} : dossier cible introuvable ({p.LinkTarget}), seul le lien est sauvegardé.");
            }
            log?.Report($"  {p.EntryName} {p.Version}");
        }
        manifest.PluginPackages = plugins;
        manifest.Sections.Plugins = true;
    }

    private static void AddDirectory(ZipArchive zip, BackupManifest manifest, string dir, string prefix, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            using var s = FileUtil.OpenShared(file);
            AddStream(zip, manifest, prefix + FileUtil.ToArchivePath(dir, file), s);
        }
    }

    private static void AddText(ZipArchive zip, BackupManifest manifest, string name, string text)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
        AddStream(zip, manifest, name, ms);
    }

    private static void AddStream(ZipArchive zip, BackupManifest manifest, string name, Stream source)
    {
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        byte[] data = ms.ToArray();
        manifest.Files[name] = FileUtil.Sha256(data);
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var es = entry.Open();
        es.Write(data);
    }

    public static BackupManifest ReadManifest(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry(BackupManifest.FileName) ?? throw new InvalidDataException("Archive sans manifest.json : ce n'est pas une sauvegarde Logi.");
        using var s = entry.Open();
        return JsonSerializer.Deserialize<BackupManifest>(s, BackupManifest.Json) ?? throw new InvalidDataException("manifest.json illisible.");
    }
}
