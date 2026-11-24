using System.Diagnostics;
using System.Management;

namespace LogiBackup.Core.Processes;

public sealed record LogiProcessInfo(int Id, string Name, string? ExecutablePath, string? CommandLine, int ParentId)
{
    /// <summary>The Options+ window process (Electron main process: no --type= argument).</summary>
    public bool IsOptionsPlusUi =>
        Name.Equals("logioptionsplus.exe", StringComparison.OrdinalIgnoreCase) &&
        !(CommandLine ?? "").Contains("--type=", StringComparison.OrdinalIgnoreCase);
}

public sealed record LogiServiceInfo(string Name, string DisplayName, string State, string StartMode, string? PathName);

/// <summary>What was running before a stop, so that the same state can be brought back.</summary>
public sealed class StopState
{
    public List<string> StoppedServices { get; } = [];
    public string? UiExecutable { get; set; }
    public string? AgentExecutable { get; set; }
}

/// <summary>Stops and restarts Logi Options+. Replaced by a fake in tests.</summary>
public interface IProcessControl
{
    Task<StopState> StopAllAsync(CancellationToken ct = default);
    Task RestartAsync(StopState state, CancellationToken ct = default);
}

/// <summary>
/// Finds and stops the Logi Options+ processes and services. Nothing is hard-coded by name only:
/// a process or service belongs to Logi when its executable lives in an install folder
/// (Options+ or Plugin Service). Names are a fallback for processes whose path cannot be read
/// without admin rights (the updater runs as LocalSystem).
///
/// Observed tree on Options+ 2.9: service OptionsPlusUpdaterService (LocalSystem)
///   -> logioptionsplus_agent.exe (user) -> LogiPluginService.exe -> LogiPluginServiceExt.exe.
/// Stopping the service is required, otherwise it starts the agent again.
/// </summary>
public sealed class LogiProcessController(LogiPaths paths, IProgress<string>? log = null) : IProcessControl
{
    private static readonly string[] FallbackNamePrefixes = ["logioptionsplus", "logipluginservice", "logi_crashpad_handler"];

    private IEnumerable<string> InstallDirs =>
        new[] { paths.OptionsPlusInstallDir, paths.PluginServiceInstallDir }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.TrimEnd('\\') + "\\");

    private bool BelongsToLogi(string? exePath, string name)
    {
        if (!string.IsNullOrEmpty(exePath))
            return InstallDirs.Any(d => exePath.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        return FallbackNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public List<LogiProcessInfo> GetProcesses()
    {
        var list = new List<LogiProcessInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, Name, ExecutablePath, CommandLine, ParentProcessId FROM Win32_Process");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                string name = mo["Name"] as string ?? "";
                string? exe = mo["ExecutablePath"] as string;
                if (!BelongsToLogi(exe, name)) continue;
                list.Add(new LogiProcessInfo(Convert.ToInt32(mo["ProcessId"]), name, exe,
                    mo["CommandLine"] as string, Convert.ToInt32(mo["ParentProcessId"])));
            }
        }
        return list;
    }

    public List<LogiServiceInfo> GetServices()
    {
        var list = new List<LogiServiceInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, PathName FROM Win32_Service");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                string? path = (mo["PathName"] as string)?.Trim('"', ' ');
                if (path is null || !InstallDirs.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new LogiServiceInfo(mo["Name"] as string ?? "", mo["DisplayName"] as string ?? "",
                    mo["State"] as string ?? "", mo["StartMode"] as string ?? "", mo["PathName"] as string));
            }
        }
        return list;
    }

    /// <summary>Stops the Logi services, then every remaining Logi process. Requires administrator rights.</summary>
    public async Task<StopState> StopAllAsync(CancellationToken ct = default)
    {
        var state = new StopState();
        var before = GetProcesses();
        state.UiExecutable = before.FirstOrDefault(p => p.IsOptionsPlusUi)?.ExecutablePath;
        state.AgentExecutable = before.FirstOrDefault(p =>
            p.Name.Equals("logioptionsplus_agent.exe", StringComparison.OrdinalIgnoreCase))?.ExecutablePath;

        foreach (var svc in GetServices().Where(s => s.State.Equals("Running", StringComparison.OrdinalIgnoreCase)))
        {
            log?.Report($"Arrêt du service {svc.Name}...");
            InvokeServiceMethod(svc.Name, "StopService");
            state.StoppedServices.Add(svc.Name);
            await WaitForServiceStateAsync(svc.Name, "Stopped", TimeSpan.FromSeconds(30), ct);
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            var remaining = GetProcesses();
            if (remaining.Count == 0) break;
            foreach (var p in remaining)
            {
                try
                {
                    using var proc = Process.GetProcessById(p.Id);
                    log?.Report($"Arrêt de {p.Name} (PID {p.Id})");
                    proc.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { /* already gone */ }
                catch (InvalidOperationException) { /* already gone */ }
                catch (System.ComponentModel.Win32Exception ex) { log?.Report($"Accès refusé à {p.Name} : {ex.Message}"); }
            }
            await Task.Delay(1000, ct);
        }

        var still = GetProcesses();
        if (still.Count > 0)
            throw new InvalidOperationException("Impossible d'arrêter : " + string.Join(", ", still.Select(p => $"{p.Name} ({p.Id})")) +
                                                ". Relancez l'application en administrateur.");
        log?.Report("Tous les processus Logi sont arrêtés.");
        return state;
    }

    /// <summary>Brings back what <see cref="StopAllAsync"/> stopped.</summary>
    public async Task RestartAsync(StopState state, CancellationToken ct = default)
    {
        foreach (var svc in state.StoppedServices)
        {
            log?.Report($"Démarrage du service {svc}...");
            InvokeServiceMethod(svc, "StartService");
        }

        // The service starts the agent. Without service, start the agent ourselves.
        if (state.AgentExecutable is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(state.StoppedServices.Count > 0 ? 20 : 0);
            while (DateTime.UtcNow < deadline && !IsRunning("logioptionsplus_agent.exe"))
                await Task.Delay(1000, ct);
            if (!IsRunning("logioptionsplus_agent.exe")) StartUnelevated(state.AgentExecutable);
        }

        if (state.UiExecutable is not null && !GetProcesses().Any(p => p.IsOptionsPlusUi))
            StartUnelevated(state.UiExecutable);
    }

    private bool IsRunning(string name) =>
        GetProcesses().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts a program through Explorer so that it does not inherit the administrator token.</summary>
    private void StartUnelevated(string exe)
    {
        log?.Report($"Lancement de {Path.GetFileName(exe)}");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false })?.Dispose();
    }

    private static void InvokeServiceMethod(string serviceName, string method)
    {
        using var svc = new ManagementObject($"Win32_Service.Name='{serviceName.Replace("'", "")}'");
        var code = Convert.ToUInt32(svc.InvokeMethod(method, null));
        // 0 = success, 10 = already running (start), 5 = cannot accept control (already stopped)
        if (code is not (0 or 5 or 10))
            throw new InvalidOperationException($"{method} sur le service {serviceName} a échoué (code WMI {code}). Droits administrateur requis.");
    }

    private static async Task WaitForServiceStateAsync(string serviceName, string expected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var svc = new ManagementObject($"Win32_Service.Name='{serviceName.Replace("'", "")}'");
            svc.Get();
            if (string.Equals(svc["State"] as string, expected, StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(500, ct);
        }
    }
}
