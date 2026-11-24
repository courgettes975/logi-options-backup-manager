using Microsoft.Win32;

namespace LogiBackup.Core;

/// <summary>Small read helpers over the Windows registry (64-bit view).</summary>
public static class RegistryReader
{
    public static string? ReadLocalMachineString(string subKey, string name)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    /// <summary>Finds an installed product by exact DisplayName in the Uninstall keys (HKLM 64/32, HKCU).</summary>
    public static InstalledProduct? FindInstalledProduct(string displayName)
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var roots = new (RegistryHive hive, RegistryView view)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };
        foreach (var (hive, view) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(uninstall);
            if (root is null) continue;
            foreach (string sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is string name &&
                    string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    return new InstalledProduct(name, k.GetValue("DisplayVersion") as string,
                        k.GetValue("InstallLocation") as string, $@"{hive}\{uninstall}\{sub}");
                }
            }
        }
        return null;
    }
}

public sealed record InstalledProduct(string Name, string? Version, string? InstallLocation, string RegistryKey);
