using Microsoft.Win32;

namespace LogiBackup.Core.Backup;

public sealed record RegistryValueDto(string Name, string Kind, string Value);

/// <summary>
/// HKCU\Software\Logitech\SharedSettings: the only registry key found with portable content
/// (enable_lps_in_background). The \Data keys hold DPAPI blobs and are never touched.
/// </summary>
public static class SharedSettingsRegistry
{
    public const string SubKey = @"Software\Logitech\SharedSettings";

    public static List<RegistryValueDto> Export()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKey);
        if (key is null) return [];
        var list = new List<RegistryValueDto>();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            if (kind is RegistryValueKind.DWord or RegistryValueKind.QWord or RegistryValueKind.String)
                list.Add(new RegistryValueDto(name, kind.ToString(), Convert.ToString(key.GetValue(name), System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        }
        return list;
    }

    public static int Import(IEnumerable<RegistryValueDto> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SubKey);
        int n = 0;
        foreach (var v in values)
        {
            if (!Enum.TryParse<RegistryValueKind>(v.Kind, out var kind)) continue;
            object? data = kind switch
            {
                RegistryValueKind.DWord => int.Parse(v.Value, System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => long.Parse(v.Value, System.Globalization.CultureInfo.InvariantCulture),
                RegistryValueKind.String => v.Value,
                _ => null,
            };
            if (data is null) continue;
            key.SetValue(v.Name, data, kind);
            n++;
        }
        return n;
    }
}
