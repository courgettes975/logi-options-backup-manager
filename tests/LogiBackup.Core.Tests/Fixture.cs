using System.Text;
using System.Text.Json.Nodes;
using LogiBackup.Core;
using LogiBackup.Core.Processes;
using Microsoft.Data.Sqlite;

namespace LogiBackup.Core.Tests;

/// <summary>A fake Logi installation in a temp folder, with the schema observed on Options+ 2.9.</summary>
public sealed class FakeLogi : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "logibak-tests", Guid.NewGuid().ToString("N"));
    public LogiPaths Paths { get; }

    public FakeLogi(string userName, string json, int schema = 26)
    {
        string profile = Path.Combine(Root, "Users", userName);
        Paths = new LogiPaths(
            Path.Combine(profile, "Local", "LogiOptionsPlus"),
            Path.Combine(profile, "Local", "Logi", "LogiPluginService"),
            Path.Combine(profile, "Roaming", "logioptionsplus"),
            Path.Combine(Root, "NoInstall", "LogiOptionsPlus"),
            Path.Combine(Root, "NoInstall", "LogiPluginService"),
            profile);
        Directory.CreateDirectory(Paths.OptionsPlusLocal);
        CreateSettingsDb(Paths.SettingsDb, json.Replace("\"schema_version\": 26", $"\"schema_version\": {schema}"));
    }

    public static void CreateSettingsDb(string path, string json)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE data(_id INTEGER PRIMARY KEY,_date_created datetime default current_timestamp,file BLOB NOT NULL);
            CREATE TABLE snapshots(_id INTEGER PRIMARY KEY,_date_created datetime default current_timestamp,uuid TEXT NOT NULL,label TEXT NOT NULL,file BLOB NOT NULL);
            INSERT INTO data(_id, file) VALUES (1, $f);
            """;
        cmd.Parameters.Add("$f", SqliteType.Blob).Value = Encoding.UTF8.GetBytes(json);
        cmd.ExecuteNonQuery();
    }

    public void AddCreativeProfile(string deviceType, string app, string profileJson)
    {
        string dir = Path.Combine(Paths.PluginApplications, deviceType, app);
        Directory.CreateDirectory(Path.Combine(dir, "Profiles", "P1"));
        File.WriteAllText(Path.Combine(dir, "ApplicationInfo.json"), "{\"name\":\"" + app + "\"}");
        File.WriteAllText(Path.Combine(dir, "Profiles", "P1", "ProfileInfo.json"), profileJson);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }

    public static string SampleJson(string serial, string userProfile, string profileId, string extraKey = "") => $$"""
        {
          "schema_version": 26,
          "theme": "DARK_MODE",
          "accounts_refresh_token_expiration": 123,
          "dfu/{{serial}}/last_update_check": {"x": 1},
          "ever_connected_devices": {"devices": [{"deviceModel": "2b378", "deviceType": "KEYBOARD", "serialNumber": "{{serial}}", "slotPrefix": "mx-keys-s-2b378", "connectionType": "BLE"}]},
          "slot_prefixes_ever_seen": ["mx-keys-s-2b378"],
          "migration_report": {"devices": [{"deviceName": "MX Keys S", "modelId": "2b378"}]},
          "profile_keys": ["profile-{{profileId}}"],
          "profile-{{profileId}}": {"id": "{{profileId}}", "assignments": [{"slotId": "mx-keys-s-2b378_c11", "cardId": "card_{{serial}}"}, {"slotId": "mx-vertical-eb020_c82", "cardId": "x"}]},
          "applications": {"applications": [{"applicationId": "app1", "applicationPath": "{{userProfile.Replace("\\", "\\\\")}}\\AppData\\Local\\Tool\\tool.exe", "processId": 42, "sessionId": 8, "commandLine": "tool --secret"}]}{{extraKey}}
        }
        """;
}

/// <summary>Records calls instead of stopping real processes.</summary>
public sealed class FakeProcessControl : IProcessControl
{
    public int Stops, Restarts;
    public Task<StopState> StopAllAsync(CancellationToken ct = default) { Stops++; return Task.FromResult(new StopState()); }
    public Task RestartAsync(StopState state, CancellationToken ct = default) { Restarts++; return Task.CompletedTask; }
}
