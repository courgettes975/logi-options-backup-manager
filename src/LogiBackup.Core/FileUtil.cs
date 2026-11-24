using System.Security.Cryptography;

namespace LogiBackup.Core;

public static class FileUtil
{
    /// <summary>Opens a file that another process (Options+, Plugin Service) may hold open.</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static string Sha256(Stream s) => Convert.ToHexString(SHA256.HashData(s));

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Relative path with forward slashes, as stored in zip archives.</summary>
    public static string ToArchivePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    /// <summary>Text files of the Plugin Service that may contain a user path to rewrite.</summary>
    public static bool IsRewritableText(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
    }
}
