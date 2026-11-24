using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBackup.Core;
using LogiBackup.Core.Backup;
using LogiBackup.Core.Detection;
using LogiBackup.Core.Plugins;
using LogiBackup.Core.Settings;
using Microsoft.Win32;

namespace LogiBackup.App;

public sealed record Row(string Label, string Value);

public partial class MainViewModel : ObservableObject
{
    private readonly LogiPaths _paths = LogiPaths.FromEnvironment();
    private readonly PortableStorage _storage = PortableStorage.Resolve();
    private readonly string _logFile;
    private readonly IProgress<string> _log;

    public ObservableCollection<string> Log { get; } = [];

    public MainViewModel()
    {
        Directory.CreateDirectory(_storage.LogsDir);
        Directory.CreateDirectory(_storage.BackupsDir);
        _logFile = Path.Combine(_storage.LogsDir, $"{DateTime.Now:yyyyMMdd}.log");
        // Progress<T> posts back to the UI thread that created it.
        _log = new Progress<string>(WriteLog);
        BackupPath = Path.Combine(_storage.BackupsDir,
            $"LogiOptions-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}{ArchiveLayout.Extension}");
        WriteLog($"Logi Options+ Backup Manager {BackupService.ToolVersion}. Journal : {_logFile}");
        WriteLog(_storage.IsPortable
            ? $"Mode portable : sauvegardes, journaux et copies de sécurité dans {_storage.Root}"
            : $"Dossier de l'application en lecture seule : fichiers écrits dans {_storage.Root}");
    }

    private void WriteLog(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Log.Add(line);
        try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch (IOException) { }
    }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DetectCommand), nameof(BackupCommand), nameof(AnalyzeArchiveCommand), nameof(RestoreCommand))]
    private bool _isBusy;

    // ---------- Analyse ----------

    [ObservableProperty] private DetectionReport? _report;
    public ObservableCollection<Row> Summary { get; } = [];
    public ObservableCollection<KeyClassification> SettingsKeys { get; } = [];

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task DetectAsync()
    {
        await RunAsync("Analyse du PC", async () =>
        {
            var r = await Task.Run(() => EnvironmentDetector.Detect(_paths));
            Report = r;
            Summary.Clear();
            Summary.Add(new("Compte Windows", r.UserName + (r.IsElevated ? " (administrateur)" : " (pas administrateur : la restauration sera refusée)")));
            Summary.Add(new("Logi Options+", r.OptionsPlus?.Version ?? "non installé"));
            Summary.Add(new("Logi Plugin Service", r.PluginService?.Version ?? "non installé"));
            Summary.Add(new("settings.db", r.Settings.FormatOk ? $"OK, schéma {r.Settings.SchemaVersion}, {r.Settings.Keys.Count} clés" : r.Settings.Error ?? "?"));
            Summary.Add(new("Périphériques", string.Join(", ", r.Devices.Select(d => d.Name))));
            Summary.Add(new("MX Creative Console", string.Join(", ", r.CreativeConsole.Select(c => $"{c.DeviceType} ({c.Applications} app., {c.Profiles} profils)"))));
            Summary.Add(new("Plugins", r.Plugins.Count.ToString()));
            Summary.Add(new("Processus Logi actifs", string.Join(", ", r.Processes.Select(p => p.Name).Distinct())));
            Summary.Add(new("Services Logi", string.Join(", ", r.Services.Select(s => $"{s.Name} ({s.State})"))));
            SettingsKeys.Clear();
            foreach (var k in r.SettingsKeys.OrderBy(k => k.Category).ThenBy(k => k.Key)) SettingsKeys.Add(k);
            foreach (var w in r.Warnings) WriteLog("Attention : " + w);
        });
    }

    // ---------- Sauvegarde ----------

    [ObservableProperty] private string _backupPath;
    [ObservableProperty] private bool _backupOptionsPlus = true;
    [ObservableProperty] private bool _backupCreative = true;
    [ObservableProperty] private bool _backupMedia = true;
    [ObservableProperty] private bool _backupRegistry = true;
    [ObservableProperty] private bool _backupPlugins = true;
    [ObservableProperty] private bool _backupStopProcesses;

    [RelayCommand]
    private void BrowseBackup()
    {
        var dlg = new SaveFileDialog
        {
            Filter = $"Sauvegarde Logi (*{ArchiveLayout.Extension})|*{ArchiveLayout.Extension}",
            FileName = Path.GetFileName(BackupPath),
            InitialDirectory = Path.GetDirectoryName(BackupPath),
        };
        if (dlg.ShowDialog() == true) BackupPath = dlg.FileName;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task BackupAsync()
    {
        var options = new BackupOptions
        {
            OptionsPlus = BackupOptionsPlus, CreativeConsole = BackupCreative, Media = BackupMedia,
            Registry = BackupRegistry, Plugins = BackupPlugins, StopProcesses = BackupStopProcesses,
        };
        await RunAsync("Sauvegarde", async () =>
        {
            var result = await Task.Run(() => new BackupService(_paths, _log).CreateAsync(BackupPath, options));
            WriteLog($"Terminé : {result.Manifest.Files.Count} fichiers, {result.SizeBytes / 1024.0:N0} Ko.");
            MessageBox.Show($"Sauvegarde créée :\n{result.ArchivePath}", "Sauvegarde", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    // ---------- Restauration ----------

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AnalyzeArchiveCommand))] private string _archivePath = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreCommand))] private RestorePreview? _preview;
    public ObservableCollection<Row> PreviewRows { get; } = [];
    public ObservableCollection<string> PreviewMessages { get; } = [];
    [ObservableProperty] private bool _restoreOptionsPlus = true;
    [ObservableProperty] private bool _restoreCreative = true;
    [ObservableProperty] private bool _restoreMedia = true;
    [ObservableProperty] private bool _restoreRegistry = true;
    [ObservableProperty] private bool _restorePlugins = true;
    [ObservableProperty] private bool _replaceExistingPlugins;
    public ObservableCollection<PluginPlanItem> PluginPlan { get; } = [];

    partial void OnReplaceExistingPluginsChanged(bool value) => RefreshPluginPlan();

    private void RefreshPluginPlan()
    {
        PluginPlan.Clear();
        if (Preview is null) return;
        foreach (var i in ReplaceExistingPlugins ? Preview.PluginPlanReplace : Preview.PluginPlanKeep) PluginPlan.Add(i);
    }
    [ObservableProperty] private bool _restoreFull;
    [ObservableProperty] private bool _forceSchema;

    [RelayCommand]
    private void BrowseArchive()
    {
        var dlg = new OpenFileDialog { InitialDirectory = _storage.BackupsDir, Filter = $"Sauvegarde Logi (*{ArchiveLayout.Extension})|*{ArchiveLayout.Extension}|Tous les fichiers|*.*" };
        if (dlg.ShowDialog() == true) { ArchivePath = dlg.FileName; AnalyzeArchiveCommand.Execute(null); }
    }

    private bool CanAnalyze() => !IsBusy && File.Exists(ArchivePath);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeArchiveAsync()
    {
        await RunAsync("Analyse de la sauvegarde", async () =>
        {
            var p = await Task.Run(() => new RestoreService(_paths, _log).Analyze(ArchivePath));
            Preview = p;
            var m = p.Manifest;
            PreviewRows.Clear();
            PreviewRows.Add(new("Créée le", $"{m.CreatedAt:g} sur {m.MachineName} ({m.UserName})"));
            PreviewRows.Add(new("Options+ / schéma", $"{m.OptionsPlusVersion} / {m.SettingsSchemaVersion}"));
            PreviewRows.Add(new("Plugin Service", m.PluginServiceVersion ?? "-"));
            PreviewRows.Add(new("Périphériques", string.Join(", ", m.Devices.Select(d => d.Name))));
            PreviewRows.Add(new("Creative Console", string.Join(", ", m.CreativeConsoleDeviceTypes)));
            PreviewRows.Add(new("Même PC et compte", p.SameAccount ? "oui (restauration complète possible)" : "non (fusion seulement)"));
            if (p.Merge is { } mg)
                PreviewRows.Add(new("Changements Options+", $"{mg.Replaced.Count} remplacées, {mg.Added.Count} ajoutées, {mg.Removed.Count} profils retirés"));
            PreviewMessages.Clear();
            foreach (var b in p.Blockers) PreviewMessages.Add("BLOQUANT : " + b);
            foreach (var w in p.Warnings) PreviewMessages.Add("Attention : " + w);
            if (PreviewMessages.Count == 0) PreviewMessages.Add("Aucun problème détecté.");
            if (!p.SameAccount) RestoreFull = false;
            if (m.Sections.Plugins)
                PreviewRows.Add(new("Plugins", $"{m.PluginPackages.Count} dans la sauvegarde ({m.PluginPackages.Sum(x => x.SizeBytes) / 1048576.0:N0} Mo)"));
            RefreshPluginPlan();
        });
    }

    private RestoreOptions CurrentRestoreOptions() => new()
    {
        Mode = RestoreFull ? RestoreMode.FullSameAccount : RestoreMode.Merge,
        OptionsPlus = RestoreOptionsPlus, CreativeConsole = RestoreCreative, Media = RestoreMedia,
        Registry = RestoreRegistry, Plugins = RestorePlugins, ReplaceExistingPlugins = ReplaceExistingPlugins,
        ForceSchemaMismatch = ForceSchema,
    };

    private bool CanRestore() => !IsBusy && Preview is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var options = CurrentRestoreOptions();
        if (!Preview!.CanRestore(options))
        {
            MessageBox.Show("La restauration est bloquée. Lisez les messages de l'analyse.", "Restauration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(
            "Logi Options+ et le Plugin Service vont être fermés, puis relancés.\n" +
            "Une copie de sécurité de vos réglages actuels sera faite avant toute modification.\n\nContinuer ?",
            "Restauration", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        await RunAsync("Restauration", async () =>
        {
            var r = await Task.Run(() => new RestoreService(_paths, _log, safetyBackupDir: _storage.SafetyDir).RestoreAsync(ArchivePath, options));
            WriteLog($"Copie de sécurité : {r.SafetyBackupPath}");
            MessageBox.Show("Restauration terminée.\n\nCopie de sécurité de l'état précédent :\n" + r.SafetyBackupPath +
                            "\n\nReconnectez-vous à votre compte Logitech dans Options+ si besoin.",
                "Restauration", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    [RelayCommand]
    private void OpenLogFolder() =>
        System.Diagnostics.Process.Start("explorer.exe", $"\"{_storage.Root}\"");

    private bool NotBusy() => !IsBusy;

    private async Task RunAsync(string title, Func<Task> action)
    {
        IsBusy = true;
        WriteLog($"--- {title} ---");
        try { await action(); }
        catch (Exception ex)
        {
            WriteLog("ERREUR : " + ex.Message);
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }
}
