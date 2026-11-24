using System.Management;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using LogiBackup.Core.Processes;
using LogiBackup.Core.Settings;

namespace LogiBackup.Core.Detection;

public sealed record DataLocation(string Name, string Path, bool Exists, long SizeBytes, string Role, bool Backed);

public sealed record CreativeDeviceFolder(string DeviceType, int Applications, int Profiles);

public sealed class DetectionReport
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string MachineName { get; init; } = Environment.MachineName;
    public string UserName { get; init; } = "";
    public string UserSidHash { get; init; } = "";
    public bool IsElevated { get; init; }
    public string? InteractiveUser { get; init; }
    public InstalledProduct? OptionsPlus { get; init; }
    public InstalledProduct? PluginService { get; init; }
    public SettingsDbInfo Settings { get; init; } = new();
    public List<KeyClassification> SettingsKeys { get; init; } = [];
    public List<DeviceInfo> Devices { get; init; } = [];
    public List<CreativeDeviceFolder> CreativeConsole { get; init; } = [];
    public List<string> Plugins { get; init; } = [];
    public List<DataLocation> Locations { get; init; } = [];
    public List<LogiProcessInfo> Processes { get; init; } = [];
    public List<LogiServiceInfo> Services { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool CanBackup => Settings.FormatOk || CreativeConsole.Count > 0;
}

/// <summary>
/// Looks at the current PC and reports what is installed, where the data lives and in which
/// format. Read-only: nothing is stopped, written or deleted.
/// </summary>
public static class EnvironmentDetector
{
    public static DetectionReport Detect(LogiPaths paths)
    {
        var warnings = new List<string>();
        var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string? interactive = TryGetInteractiveUser();
        if (interactive is not null && !string.Equals(interactive, identity.Name, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"L'application tourne sous {identity.Name} mais la session ouverte est celle de {interactive}. " +
                         "Les dossiers analysés sont ceux du compte qui exécute l'application.");

        var options = RegistryReader.FindInstalledProduct("Logi Options+");
        var plugin = RegistryReader.FindInstalledProduct("Logi Plugin Service");
        if (options is null) warnings.Add("Logi Options+ n'est pas installé (aucune entrée de désinstallation).");

        var settings = SettingsDbStore.Inspect(paths.SettingsDb);
        var keys = new List<KeyClassification>();
        var devices = new List<DeviceInfo>();
        if (settings.Json is JsonObject json)
        {
            keys = settings.Keys.Select(SettingsClassifier.Classify).ToList();
            devices = SettingsMerger.Devices(json);
            int unknown = keys.Count(k => k.Category == KeyCategory.Unknown);
            if (unknown > 0)
                warnings.Add($"{unknown} clé(s) inconnue(s) dans settings.db : elles ne seront pas restaurées.");
        }
        else if (settings.Exists)
            warnings.Add($"settings.db illisible : {settings.Error}");

        var controller = new LogiProcessController(paths);
        List<LogiProcessInfo> processes = [];
        List<LogiServiceInfo> services = [];
        try { processes = controller.GetProcesses(); services = controller.GetServices(); }
        catch (ManagementException ex) { warnings.Add("WMI indisponible : " + ex.Message); }

        return new DetectionReport
        {
            UserName = identity.Name,
            UserSidHash = HashSid(identity.User?.Value),
            IsElevated = elevated,
            InteractiveUser = interactive,
            OptionsPlus = options,
            PluginService = plugin,
            Settings = settings,
            SettingsKeys = keys,
            Devices = devices,
            CreativeConsole = ScanCreativeConsole(paths.PluginApplications),
            Plugins = ListPlugins(paths.PluginPlugins),
            Locations = BuildLocations(paths),
            Processes = processes,
            Services = services,
            Warnings = warnings,
        };
    }

    public static string HashSid(string? sid) =>
        sid is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));

    public static List<CreativeDeviceFolder> ScanCreativeConsole(string applicationsDir)
    {
        if (!Directory.Exists(applicationsDir)) return [];
        return Directory.GetDirectories(applicationsDir)
            .Select(d => new CreativeDeviceFolder(
                Path.GetFileName(d),
                Directory.GetDirectories(d).Count(a => File.Exists(Path.Combine(a, "ApplicationInfo.json"))),
                Directory.GetFiles(d, "ProfileInfo.json", SearchOption.AllDirectories).Length))
            .Where(f => f.Applications > 0 || f.Profiles > 0)
            .OrderBy(f => f.DeviceType)
            .ToList();
    }

    public static List<string> ListPlugins(string pluginsDir) =>
        Directory.Exists(pluginsDir)
            ? Directory.GetFileSystemEntries(pluginsDir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToList()
            : [];

    private static List<DataLocation> BuildLocations(LogiPaths p)
    {
        DataLocation Loc(string name, string path, string role, bool backed) =>
            new(name, path, File.Exists(path) || Directory.Exists(path), SizeOf(path), role, backed);

        return
        [
            Loc("settings.db", p.SettingsDb, "Réglages Options+ (JSON dans SQLite)", true),
            Loc("cc_config.json", p.CcConfig, "Interface Creative Console", true),
            Loc("Applications", p.PluginApplications, "Profils MX Creative Console", true),
            Loc("Media", p.PluginMedia, "Icônes et sons utilisateur", true),
            Loc("Plugins", p.PluginPlugins, "Plugins installés (liste seulement)", false),
            Loc("Applications.Backups", p.PluginApplicationsBackups, "Sauvegardes natives du Plugin Service", false),
            Loc("Profil Electron", p.OptionsPlusRoaming, "Caches et cookies, jamais copié", false),
        ];
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }

    /// <summary>Owner of explorer.exe in the current session, i.e. the user who sits at the screen.</summary>
    private static string? TryGetInteractiveUser()
    {
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE Name='explorer.exe' AND SessionId={session}");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var args = new object[2];
                    if (Convert.ToInt32(mo.InvokeMethod("GetOwner", args)) == 0 && args[0] is string user)
                        return args[1] is string domain && domain.Length > 0 ? $"{domain}\\{user}" : user;
                }
            }
        }
        catch (ManagementException) { }
        return null;
    }
}
