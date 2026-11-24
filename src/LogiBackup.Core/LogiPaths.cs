namespace LogiBackup.Core;

/// <summary>
/// Candidate locations of Logi Options+ data for one Windows user.
/// Every location is only a candidate: the detector verifies existence and format
/// before anything is read. Tests build an instance pointing to a temporary copy.
/// </summary>
public sealed record LogiPaths(
    string OptionsPlusLocal,
    string PluginServiceLocal,
    string OptionsPlusRoaming,
    string OptionsPlusInstallDir,
    string PluginServiceInstallDir,
    string UserProfile)
{
    public string SettingsDb => Path.Combine(OptionsPlusLocal, "settings.db");
    public string CcConfig => Path.Combine(OptionsPlusLocal, "cc_config.json");
    public string PluginApplications => Path.Combine(PluginServiceLocal, "Applications");
    public string PluginApplicationsBackups => Path.Combine(PluginServiceLocal, "Applications.Backups");
    public string PluginMedia => Path.Combine(PluginServiceLocal, "Media");
    public string PluginPlugins => Path.Combine(PluginServiceLocal, "Plugins");

    /// <summary>Builds the candidate paths of the current user from the environment and the registry.</summary>
    public static LogiPaths FromEnvironment()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The installer writes its real location here (observed on Options+ 2.9).
        string optionsInstall = RegistryReader.ReadLocalMachineString(@"SOFTWARE\Logitech\LogiOptionsPlus", "MainAppPath")
                                ?? Path.Combine(programFiles, "LogiOptionsPlus");

        return new LogiPaths(
            Path.Combine(local, "LogiOptionsPlus"),
            Path.Combine(local, "Logi", "LogiPluginService"),
            Path.Combine(roaming, "logioptionsplus"),
            optionsInstall,
            Path.Combine(programFiles, "Logi", "LogiPluginService"),
            profile);
    }
}
