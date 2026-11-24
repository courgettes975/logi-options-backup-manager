using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LogiBackup.Core.Settings;

/// <summary>
/// Access to Logi Options+ <c>settings.db</c>.
/// Observed format (Options+ 2.9, schema_version 26): SQLite in WAL mode, table
/// <c>data(_id INTEGER PRIMARY KEY, _date_created datetime, file BLOB NOT NULL)</c>
/// holding exactly one row whose BLOB is a UTF-8 JSON document.
/// Every method checks this format first and refuses anything else.
/// </summary>
public static class SettingsDbStore
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly string[] RequiredColumns = ["_id", "_date_created", "file"];

    public static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static bool HasSqliteHeader(string path)
    {
        if (!File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[SqliteHeader.Length];
        return fs.Read(buf, 0, buf.Length) == buf.Length && buf.AsSpan().SequenceEqual(SqliteHeader);
    }

    /// <summary>Checks the file and returns what it contains, without throwing.</summary>
    public static SettingsDbInfo Inspect(string dbPath)
    {
        var info = new SettingsDbInfo { Path = dbPath, Exists = File.Exists(dbPath) };
        if (!info.Exists) { info.Error = "Fichier absent."; return info; }
        info.IsSqlite = HasSqliteHeader(dbPath);
        if (!info.IsSqlite) { info.Error = "Le fichier n'est pas une base SQLite."; return info; }
        try
        {
            var (rowId, json) = ReadInternal(dbPath, readOnly: true);
            info.RowId = rowId;
            info.FormatOk = true;
            info.SchemaVersion = json["schema_version"]?.GetValue<int>();
            info.Keys = json.Select(p => p.Key).ToList();
            info.Json = json;
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
        }
        return info;
    }

    /// <summary>Reads the JSON document. Safe while Options+ runs (SQLite gives a consistent WAL snapshot).</summary>
    public static JsonObject ReadJson(string dbPath) => ReadInternal(dbPath, readOnly: true).json;

    /// <summary>Replaces the JSON document in one transaction, then folds the WAL into the main file.</summary>
    public static void WriteJson(string dbPath, JsonObject json)
    {
        var (rowId, _) = ReadInternal(dbPath, readOnly: false);
        byte[] bytes = Encoding.UTF8.GetBytes(json.ToJsonString(WriteOptions));

        using var con = Open(dbPath, readOnly: false);
        using (var tx = con.BeginTransaction())
        {
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE data SET file = $file WHERE _id = $id";
            cmd.Parameters.Add("$file", SqliteType.Blob).Value = bytes;
            cmd.Parameters.AddWithValue("$id", rowId);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("La mise à jour de settings.db n'a touché aucune ligne.");
            tx.Commit();
        }
        Checkpoint(con);
    }

    /// <summary>Folds pending WAL pages into the main database file.</summary>
    public static void Checkpoint(string dbPath)
    {
        using var con = Open(dbPath, readOnly: false);
        Checkpoint(con);
    }

    private static void Checkpoint(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath, bool readOnly)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, // release the file handle on Dispose, the file is copied or replaced afterwards
        };
        var con = new SqliteConnection(csb.ToString());
        con.Open();
        return con;
    }

    private static (long rowId, JsonObject json) ReadInternal(string dbPath, bool readOnly)
    {
        if (!HasSqliteHeader(dbPath)) throw new InvalidDataException($"{dbPath} n'est pas une base SQLite.");
        using var con = Open(dbPath, readOnly);

        var columns = new List<string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('data')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(0));
        }
        var missing = RequiredColumns.Except(columns).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"Format inconnu : la table 'data' manque des colonnes {string.Join(", ", missing)}.");

        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT _id, file FROM data";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException("La table 'data' est vide. Lancez Logi Options+ une fois.");
            long id = r.GetInt64(0);
            byte[] blob = r.GetFieldValue<byte[]>(1);
            if (r.Read()) throw new InvalidDataException("Format inconnu : la table 'data' contient plusieurs lignes.");

            var node = JsonNode.Parse(blob) as JsonObject
                       ?? throw new InvalidDataException("Le contenu de settings.db n'est pas un objet JSON.");
            return (id, node);
        }
    }
}

public sealed class SettingsDbInfo
{
    public string Path { get; init; } = "";
    public bool Exists { get; set; }
    public bool IsSqlite { get; set; }
    public bool FormatOk { get; set; }
    public long RowId { get; set; }
    public int? SchemaVersion { get; set; }
    public List<string> Keys { get; set; } = [];
    public JsonObject? Json { get; set; }
    public string? Error { get; set; }
}
