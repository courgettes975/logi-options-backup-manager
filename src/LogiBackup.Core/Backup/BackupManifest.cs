using System.Text.Json;
using System.Text.Json.Serialization;
using LogiBackup.Core.Plugins;
using LogiBackup.Core.Settings;

namespace LogiBackup.Core.Backup;

/// <summary>Description of a backup archive, stored as manifest.json at its root.</summary>
public sealed class BackupManifest
{
    public const int CurrentFormat = 1;
    public const string FileName = "manifest.json";

    public int FormatVersion { get; set; } = CurrentFormat;
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    /// <summary>SHA-256 of the Windows SID. Lets a restore know it runs for the same account, without storing the SID.</summary>
    public string UserSidHash { get; set; } = "";
    public string UserProfilePath { get; set; } = "";
    public string? OptionsPlusVersion { get; set; }
    public string? PluginServiceVersion { get; set; }
    public int? SettingsSchemaVersion { get; set; }
    public List<DeviceInfo> Devices { get; set; } = [];
    public List<string> SlotPrefixesInProfiles { get; set; } = [];
    public List<string> CreativeConsoleDeviceTypes { get; set; } = [];
    public List<string> Plugins { get; set; } = [];
    /// <summary>Plugins whose files are inside the archive (Plugins section).</summary>
    public List<PluginEntry> PluginPackages { get; set; } = [];
    public BackupSections Sections { get; set; } = new();
    /// <summary>Relative path inside the archive -> SHA-256.</summary>
    public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class BackupSections
{
    public bool OptionsPlus { get; set; }
    public bool CreativeConsole { get; set; }
    public bool Media { get; set; }
    public bool Registry { get; set; }
    public bool Plugins { get; set; }
}

/// <summary>Layout of the archive. One place, shared by backup and restore.</summary>
public static class ArchiveLayout
{
    public const string SettingsFull = "optionsplus/settings.full.json";
    public const string SettingsPortable = "optionsplus/settings.portable.json";
    public const string CcConfig = "optionsplus/cc_config.json";
    public const string Applications = "creativeconsole/Applications/";
    public const string Media = "creativeconsole/Media/";
    public const string PluginList = "creativeconsole/plugins.json";
    public const string SharedSettings = "registry/shared_settings.json";
    /// <summary>plugins/installed/&lt;folder&gt;/... : copy of an installed plugin folder.</summary>
    public const string PluginsInstalled = "plugins/installed/";
    /// <summary>plugins/links/&lt;Name&gt;.link : development link files, as found.</summary>
    public const string PluginsLinks = "plugins/links/";
    /// <summary>plugins/linked/&lt;Name&gt;/... : the build folder a .link points to.</summary>
    public const string PluginsLinked = "plugins/linked/";
    public const string Extension = ".logibak";
}
