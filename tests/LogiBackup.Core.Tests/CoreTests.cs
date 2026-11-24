using System.IO.Compression;
using System.Text.Json.Nodes;
using LogiBackup.Core.Backup;
using LogiBackup.Core.Settings;

namespace LogiBackup.Core.Tests;

public class SettingsDbStoreTests
{
    [Fact]
    public void Reads_and_writes_the_json_document()
    {
        using var f = new FakeLogi("alice", FakeLogi.SampleJson("SER1", @"C:\Users\alice", Guid.NewGuid().ToString()));
        var info = SettingsDbStore.Inspect(f.Paths.SettingsDb);
        Assert.True(info.FormatOk, info.Error);
        Assert.Equal(26, info.SchemaVersion);

        var json = SettingsDbStore.ReadJson(f.Paths.SettingsDb);
        json["theme"] = "LIGHT_MODE";
        SettingsDbStore.WriteJson(f.Paths.SettingsDb, json);
        Assert.Equal("LIGHT_MODE", SettingsDbStore.ReadJson(f.Paths.SettingsDb)["theme"]!.GetValue<string>());
    }

    [Fact]
    public void Refuses_a_file_that_is_not_sqlite()
    {
        string p = Path.GetTempFileName();
        File.WriteAllText(p, "not a db");
        var info = SettingsDbStore.Inspect(p);
        Assert.False(info.FormatOk);
        Assert.False(info.IsSqlite);
        File.Delete(p);
    }
}

public class ClassifierTests
{
    [Theory]
    [InlineData("profile-420fd454-0c36-499d-bde4-146823b16147", KeyCategory.Portable)]
    [InlineData("battery/mx-keys-s-2b378/warning_notification", KeyCategory.Portable)]
    [InlineData("dfu/SERIAL0001/last_update_check", KeyCategory.DeviceSpecific)]
    [InlineData("easy_switch", KeyCategory.DeviceSpecific)]
    [InlineData("accounts_refresh_token_expiration", KeyCategory.Account)]
    [InlineData("iconsLocalPathCache", KeyCategory.Cache)]
    [InlineData("analytics_seen_devices_v2", KeyCategory.Telemetry)]
    [InlineData("something_new_in_v3", KeyCategory.Unknown)]
    public void Classifies_keys(string key, KeyCategory expected) =>
        Assert.Equal(expected, SettingsClassifier.Classify(key).Category);
}

public class MergerTests
{
    [Fact]
    public void Portable_extract_drops_serials_and_volatile_fields()
    {
        var full = (JsonObject)JsonNode.Parse(FakeLogi.SampleJson("SER1", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"))!;
        var portable = SettingsMerger.ExtractPortable(full);
        Assert.False(portable.ContainsKey("dfu/SER1/last_update_check"));
        Assert.False(portable.ContainsKey("ever_connected_devices"));
        Assert.False(portable.ContainsKey("accounts_refresh_token_expiration"));
        var app = portable["applications"]!["applications"]![0]!.AsObject();
        Assert.False(app.ContainsKey("processId"));
        Assert.False(app.ContainsKey("commandLine"));
        Assert.True(app.ContainsKey("applicationPath"));
    }

    [Fact]
    public void Merge_replaces_profiles_and_keeps_target_device_state()
    {
        var source = (JsonObject)JsonNode.Parse(FakeLogi.SampleJson("SRC", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"))!;
        var target = (JsonObject)JsonNode.Parse(FakeLogi.SampleJson("TGT", @"C:\Users\bob", "22222222-2222-2222-2222-222222222222",
            ",\n \"future_key\": 7"))!;
        target["theme"] = "LIGHT_MODE";

        var r = SettingsMerger.Merge(target, SettingsMerger.ExtractPortable(source), new PathRewriter(@"C:\Users\alice", @"C:\Users\bob"));
        var j = r.Json;

        Assert.Equal("DARK_MODE", j["theme"]!.GetValue<string>());
        Assert.True(j.ContainsKey("profile-11111111-1111-1111-1111-111111111111"));
        Assert.False(j.ContainsKey("profile-22222222-2222-2222-2222-222222222222"));
        Assert.Contains("profile-22222222-2222-2222-2222-222222222222", r.Removed);
        Assert.True(j.ContainsKey("dfu/TGT/last_update_check"));            // target serial kept
        Assert.False(j.ContainsKey("dfu/SRC/last_update_check"));           // source serial not copied
        Assert.Equal(7, j["future_key"]!.GetValue<int>());                  // unknown key untouched
        Assert.Equal(@"C:\Users\bob\AppData\Local\Tool\tool.exe",
            j["applications"]!["applications"]![0]!["applicationPath"]!.GetValue<string>());
        Assert.Equal(1, r.RewrittenPaths);
    }

    [Fact]
    public void Merge_ignores_non_portable_keys_slipped_into_a_portable_file()
    {
        var target = new JsonObject { ["schema_version"] = 26 };
        var tampered = new JsonObject { ["easy_switch"] = "x", ["theme"] = "DARK_MODE" };
        var r = SettingsMerger.Merge(target, tampered, new PathRewriter(null, null));
        Assert.False(r.Json.ContainsKey("easy_switch"));
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void Slot_prefixes_come_from_assignments()
    {
        var full = (JsonObject)JsonNode.Parse(FakeLogi.SampleJson("S", @"C:\Users\a", "11111111-1111-1111-1111-111111111111"))!;
        Assert.Equal(["mx-keys-s-2b378", "mx-vertical-eb020"], SettingsMerger.SlotPrefixesUsedByProfiles(full));
    }
}

public class PathRewriterTests
{
    [Fact]
    public void Rewrites_whole_segments_only()
    {
        var r = new PathRewriter(@"C:\Users\bob", @"D:\Profiles\alice");
        Assert.Equal(@"D:\Profiles\alice\x.exe", r.RewriteText(@"C:\Users\bob\x.exe"));
        Assert.Equal(@"C:\Users\bobby\x.exe", r.RewriteText(@"C:\Users\bobby\x.exe"));
        Assert.Equal(@"""D:\\Profiles\\alice\\x.exe""", r.RewriteText(@"""C:\\Users\\bob\\x.exe"""));
        Assert.Equal("poster://D:/Profiles/alice/a.png", r.RewriteText("poster://c:/users/BOB/a.png"));
    }

    [Fact]
    public void Same_profile_is_a_no_op() => Assert.True(new PathRewriter(@"C:\Users\a\", @"c:\users\a").IsNoOp);
}

public class BackupRestoreTests
{
    [Fact]
    public async Task Backup_then_restore_on_another_user_merges_and_keeps_a_safety_copy()
    {
        using var src = new FakeLogi("alice", FakeLogi.SampleJson("SRC", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"));
        src.AddCreativeProfile("Loupedeck70", "@_photoshop", "{\"path\": \"" + src.Paths.UserProfile.Replace("\\", "\\\\") + "\\\\a.png\"}");
        File.WriteAllText(src.Paths.CcConfig, "{\"theme\":\"dark\",\"hostId\":\"HOST-A\",\"language\":\"fr-FR\"}");

        string archive = Path.Combine(src.Root, "b" + ArchiveLayout.Extension);
        var procs = new FakeProcessControl();
        var backup = await new BackupService(src.Paths, null, procs).CreateAsync(archive, new BackupOptions { Registry = false });
        Assert.True(backup.Manifest.Sections.OptionsPlus);
        Assert.True(backup.Manifest.Sections.CreativeConsole);
        Assert.Equal(["Loupedeck70"], backup.Manifest.CreativeConsoleDeviceTypes);
        Assert.DoesNotContain(backup.Manifest.Devices, d => d.SlotPrefix == "" );

        using var tgt = new FakeLogi("bob", FakeLogi.SampleJson("TGT", @"C:\Users\bob", "22222222-2222-2222-2222-222222222222"));
        File.WriteAllText(tgt.Paths.CcConfig, "{\"theme\":\"light\",\"hostId\":\"HOST-B\"}");
        // Copy the archive so the target has no access to the source folders.
        string copy = Path.Combine(tgt.Root, "b" + ArchiveLayout.Extension);
        File.Copy(archive, copy);

        string safetyDir = Path.Combine(tgt.Root, "safety");
        var svc = new RestoreService(tgt.Paths, null, procs, safetyDir);
        var preview = svc.Analyze(copy);
        Assert.Empty(preview.Blockers);
        Assert.True(preview.NeedsPathRewrite);
        // mx-vertical is only in the assignments, never seen by the source PC: a preset, not reported.
        Assert.DoesNotContain("mx-vertical-eb020", preview.DevicesNotSeenOnTarget);

        var result = await svc.RestoreAsync(copy, new RestoreOptions { Registry = false });

        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.Equal(1, procs.Stops);
        Assert.Equal(1, procs.Restarts);
        var json = SettingsDbStore.ReadJson(tgt.Paths.SettingsDb);
        Assert.True(json.ContainsKey("profile-11111111-1111-1111-1111-111111111111"));
        Assert.True(json.ContainsKey("dfu/TGT/last_update_check"));

        var cc = JsonNode.Parse(File.ReadAllText(tgt.Paths.CcConfig))!;
        Assert.Equal("dark", cc["theme"]!.GetValue<string>());
        Assert.Equal("HOST-B", cc["hostId"]!.GetValue<string>());

        string profile = File.ReadAllText(Path.Combine(tgt.Paths.PluginApplications, "Loupedeck70", "@_photoshop", "Profiles", "P1", "ProfileInfo.json"));
        Assert.Contains(tgt.Paths.UserProfile.Replace("\\", "\\\\"), profile);
        Assert.DoesNotContain("alice", profile);
    }

    [Fact]
    public async Task Tampered_archive_is_blocked()
    {
        using var src = new FakeLogi("alice", FakeLogi.SampleJson("SRC", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"));
        string archive = Path.Combine(src.Root, "b" + ArchiveLayout.Extension);
        await new BackupService(src.Paths, null, new FakeProcessControl()).CreateAsync(archive, new BackupOptions { Registry = false, CreativeConsole = false, Media = false });

        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            zip.GetEntry(ArchiveLayout.SettingsPortable)!.Delete();
            using var w = new StreamWriter(zip.CreateEntry(ArchiveLayout.SettingsPortable).Open());
            w.Write("{\"theme\":\"HACKED\"}");
        }
        var preview = new RestoreService(src.Paths, null, new FakeProcessControl(), Path.Combine(src.Root, "s")).Analyze(archive);
        Assert.Contains(preview.Blockers, b => b.Contains(ArchiveLayout.SettingsPortable));
    }

    [Fact]
    public async Task Schema_mismatch_needs_force()
    {
        using var src = new FakeLogi("alice", FakeLogi.SampleJson("SRC", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"));
        string archive = Path.Combine(src.Root, "b" + ArchiveLayout.Extension);
        await new BackupService(src.Paths, null, new FakeProcessControl()).CreateAsync(archive, new BackupOptions { Registry = false });

        using var tgt = new FakeLogi("bob", FakeLogi.SampleJson("TGT", @"C:\Users\bob", "22222222-2222-2222-2222-222222222222"), schema: 27);
        var svc = new RestoreService(tgt.Paths, null, new FakeProcessControl(), Path.Combine(tgt.Root, "s"));
        var preview = svc.Analyze(archive);
        Assert.True(preview.SchemaMismatch);
        Assert.False(preview.CanRestore(new RestoreOptions()));
        Assert.True(preview.CanRestore(new RestoreOptions { ForceSchemaMismatch = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(archive, new RestoreOptions { Registry = false }));
    }
}

public class PortableStorageTests
{
    [Fact]
    public void Uses_the_exe_folder_when_writable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "logibak-usb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = PortableStorage.Resolve(dir);
        Assert.True(s.IsPortable);
        Assert.Equal(Path.Combine(dir, "Sauvegardes"), s.BackupsDir);
        Assert.StartsWith(Path.Combine(dir, "Donnees", "pre-restore"), s.SafetyDir);
        Assert.Empty(Directory.GetFiles(dir)); // the write probe is cleaned up
        Directory.Delete(dir, true);
    }
}

public class PluginTests
{
    private static void MakePlugin(string dir, string name, string version, string dllContent)
    {
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "metadata"));
        File.WriteAllText(Path.Combine(dir, "metadata", "LoupedeckPackage.yaml"),
            $"type: plugin4\nname: {name}\ndisplayName: {name}\n\n# Plugin version.\nversion: {version}\nsupportedDevices:\n  name: nested\n");
        File.WriteAllText(Path.Combine(dir, "bin", name + "Plugin.dll"), dllContent);
    }

    [Fact]
    public void Parses_top_level_name_and_version_only()
    {
        var (n, v) = Plugins.PluginCatalog.ParsePackage("type: plugin4\nname: VSCode\nversion: 2.2.0.4197\nx:\n  name: inner\n  version: 9\n");
        Assert.Equal("VSCode", n);
        Assert.Equal("2.2.0.4197", v);
    }

    [Fact]
    public async Task Plugins_are_saved_and_restored_keeping_or_replacing_existing_ones()
    {
        using var src = new FakeLogi("alice", FakeLogi.SampleJson("SRC", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"));
        MakePlugin(Path.Combine(src.Paths.PluginPlugins, "VSCode"), "VSCode", "2.2", "vscode-new");
        MakePlugin(Path.Combine(src.Paths.PluginPlugins, "Excel"), "Excel", "1.0", "excel");
        string devBuild = Path.Combine(src.Root, "dev", "SystemGaugePlugin", "bin", "Release");
        MakePlugin(devBuild, "SystemGauge", "1.1.0", "gauge");
        File.WriteAllText(Path.Combine(src.Paths.PluginPlugins, "SystemGaugePlugin.link"), devBuild + "\\ \r\n");

        string archive = Path.Combine(src.Root, "b" + ArchiveLayout.Extension);
        var procs = new FakeProcessControl();
        var backup = await new BackupService(src.Paths, null, procs).CreateAsync(archive, new BackupOptions { Registry = false });
        Assert.True(backup.Manifest.Sections.Plugins);
        Assert.Equal(3, backup.Manifest.PluginPackages.Count);
        Assert.Contains(backup.Manifest.PluginPackages, p => p.IsLink && p.PackageName == "SystemGauge" && p.Version == "1.1.0");

        Directory.Delete(Path.Combine(src.Root, "dev"), true); // the dev build does not exist on the new PC

        using var tgt = new FakeLogi("bob", FakeLogi.SampleJson("TGT", @"C:\Users\bob", "22222222-2222-2222-2222-222222222222"));
        MakePlugin(Path.Combine(tgt.Paths.PluginPlugins, "VSCode"), "VSCode", "2.0", "vscode-old");
        File.WriteAllText(Path.Combine(tgt.Paths.PluginPlugins, "VSCode", "bin", "stale.dll"), "x");
        var svc = new RestoreService(tgt.Paths, null, procs, Path.Combine(tgt.Root, "s"));

        var preview = svc.Analyze(archive);
        Assert.Contains(preview.PluginPlanKeep, i => i.Source.EntryName == "VSCode" && i.Action == Plugins.PluginAction.Skip);
        Assert.Contains(preview.PluginPlanKeep, i => i.Source.EntryName == "Excel" && i.Action == Plugins.PluginAction.Install);
        Assert.Contains(preview.PluginPlanKeep, i => i.Source.EntryName == "SystemGaugePlugin.link" && i.Action == Plugins.PluginAction.InstallLinkedAsFolder);

        var r1 = await svc.RestoreAsync(archive, new RestoreOptions { Registry = false });
        Assert.Equal(2, r1.Plugins);
        Assert.Equal("vscode-old", File.ReadAllText(Path.Combine(tgt.Paths.PluginPlugins, "VSCode", "bin", "VSCodePlugin.dll")));
        Assert.Equal("excel", File.ReadAllText(Path.Combine(tgt.Paths.PluginPlugins, "Excel", "bin", "ExcelPlugin.dll")));
        Assert.Equal("gauge", File.ReadAllText(Path.Combine(tgt.Paths.PluginPlugins, "SystemGauge", "bin", "SystemGaugePlugin.dll")));
        Assert.False(File.Exists(Path.Combine(tgt.Paths.PluginPlugins, "SystemGaugePlugin.link")));

        var r2 = await svc.RestoreAsync(archive, new RestoreOptions { Registry = false, ReplaceExistingPlugins = true });
        Assert.Equal("vscode-new", File.ReadAllText(Path.Combine(tgt.Paths.PluginPlugins, "VSCode", "bin", "VSCodePlugin.dll")));
        Assert.False(File.Exists(Path.Combine(tgt.Paths.PluginPlugins, "VSCode", "bin", "stale.dll"))); // folder replaced, not merged
    }

    [Fact]
    public async Task Link_is_restored_when_the_build_folder_exists_on_the_target()
    {
        using var src = new FakeLogi("alice", FakeLogi.SampleJson("SRC", @"C:\Users\alice", "11111111-1111-1111-1111-111111111111"));
        string devBuild = Path.Combine(src.Root, "dev", "Gauge");
        MakePlugin(devBuild, "SystemGauge", "1.1.0", "gauge");
        Directory.CreateDirectory(src.Paths.PluginPlugins);
        File.WriteAllText(Path.Combine(src.Paths.PluginPlugins, "SystemGaugePlugin.link"), devBuild);
        string archive = Path.Combine(src.Root, "b" + ArchiveLayout.Extension);
        await new BackupService(src.Paths, null, new FakeProcessControl()).CreateAsync(archive, new BackupOptions { Registry = false });

        using var tgt = new FakeLogi("bob", FakeLogi.SampleJson("TGT", @"C:\Users\bob", "22222222-2222-2222-2222-222222222222"));
        await new RestoreService(tgt.Paths, null, new FakeProcessControl(), Path.Combine(tgt.Root, "s"))
            .RestoreAsync(archive, new RestoreOptions { Registry = false });
        Assert.Equal(devBuild, File.ReadAllText(Path.Combine(tgt.Paths.PluginPlugins, "SystemGaugePlugin.link")).Trim());
        Assert.False(Directory.Exists(Path.Combine(tgt.Paths.PluginPlugins, "SystemGauge")));
    }
}
