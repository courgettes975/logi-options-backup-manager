namespace LogiBackup.Core;

/// <summary>
/// Where the application writes its own files. Portable by default: everything goes next to the
/// executable (a USB key for example), so nothing is left on the PC. If that folder is read-only
/// (CD, write-protected key, Program Files), it falls back to %LOCALAPPDATA%.
/// No absolute path is ever saved, so a different drive letter on another PC does not matter.
///
///   LogiOptionsBackupManager.exe
///   Sauvegardes\                     default location of the .logibak files
///   Donnees\logs\                    one log per day
///   Donnees\pre-restore\&lt;PC&gt;\    safety copies made before each restore, per computer
/// </summary>
public sealed class PortableStorage
{
    public string Root { get; }
    public bool IsPortable { get; }
    public string BackupsDir => Path.Combine(Root, "Sauvegardes");
    public string LogsDir => Path.Combine(Root, "Donnees", "logs");
    public string SafetyDir => Path.Combine(Root, "Donnees", "pre-restore", Environment.MachineName);

    private PortableStorage(string root, bool portable) { Root = root; IsPortable = portable; }

    public static PortableStorage Resolve(string? exeDir = null)
    {
        exeDir ??= AppContext.BaseDirectory; // for a single-file app: the folder of the .exe
        if (IsWritable(exeDir)) return new PortableStorage(exeDir, portable: true);
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogiOptionsBackupManager");
        return new PortableStorage(local, portable: false);
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
