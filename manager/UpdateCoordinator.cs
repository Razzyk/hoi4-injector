using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HOI4Admission
{
    internal sealed class UpdateCoordinator
    {
        private readonly Configuration config;
        private readonly string configPath;
        private readonly ModuleUpdater updater;
        private readonly Func<bool> gameRunning;
        private readonly Action stopService;
        private readonly Action<string> startService;
        private readonly object sync = new object();
        private Task<ModulePackage> check;
        private ModulePackage pending;
        private DateTime nextCheck = DateTime.MinValue;
        private string status = "Automatic DLL updates enabled";
        public string Status { get { lock (sync) return status; } }

        public UpdateCoordinator(Configuration configuration, string path, ModuleUpdater moduleUpdater)
            : this(configuration, path, moduleUpdater, null, null, null) { }
        internal UpdateCoordinator(Configuration configuration, string path, ModuleUpdater moduleUpdater,
            Func<bool> running, Action stop, Action<string> start)
        {
            config = configuration; configPath = path; updater = moduleUpdater;
            gameRunning = running ?? GameRunning;
            stopService = stop ?? StopOwnedService;
            startService = start ?? (directory => ManagerProgram.EnsureService(config, configPath, directory));
            ModulePackage current = updater.Current();
            if (!string.Equals(config.DllPath, Path.Combine(current.Directory, "HOI4Admission.dll"), StringComparison.OrdinalIgnoreCase))
            { pending = current; status = "Installed DLL ready; session migration pending"; }
        }
        public void RequestCheck() { lock (sync) nextCheck = DateTime.MinValue; }

        public void Tick()
        {
            lock (sync)
            {
                if (check != null && check.IsCompleted)
                {
                    if (check.IsFaulted)
                    {
                        Exception failure = check.Exception.GetBaseException();
                        status = "Update check unavailable; installed DLL retained: " + failure.Message;
                    }
                    else if (check.Result != null)
                    { pending = check.Result; status = "DLL " + pending.Version + " downloaded"; }
                    else status = "DLL is up to date";
                    check = null;
                }
                if (pending != null)
                {
                    if (gameRunning()) status = "DLL " + pending.Version + " ready; close HOI4 to activate";
                    else
                    {
                        try { Activate(pending); status = "DLL " + pending.Version + " installed"; pending = null; }
                        catch (Exception error) { status = "DLL update retained for retry: " + error.Message; }
                    }
                }
                if (check == null && pending == null && DateTime.UtcNow >= nextCheck)
                {
                    nextCheck = DateTime.UtcNow.AddMinutes(15);
                    status = "Checking DLL releases";
                    check = Task.Run(() => updater.CheckAndStage());
                }
            }
        }
        private static bool GameRunning()
        {
            Process[] games = Process.GetProcessesByName("hoi4");
            try { return games.Any(p => !p.HasExited); }
            catch (Exception) { return true; }
            finally { foreach (Process game in games) game.Dispose(); }
        }
        private void Activate(ModulePackage package)
        {
            // Both checks bracket the potentially slow validation. Versioned files
            // remain separate from the current DLL throughout staging.
            if (gameRunning()) throw new InvalidOperationException("Close HOI4 before activating the downloaded DLL");
            string oldDll = config.DllPath, oldManifest = config.ManifestPath;
            string oldDllHash = config.DllHash, oldManifestHash = config.ManifestHash;
            string dll = Path.Combine(package.Directory, "HOI4Admission.dll");
            string manifest = Path.Combine(package.Directory, "mod-manifest.json");
            string dllHash = Files.Hash(dll), manifestHash = Files.Hash(manifest);
            if (gameRunning()) throw new InvalidOperationException("HOI4 started; activation deferred");
            stopService();
            try
            {
                config.DllPath = dll; config.ManifestPath = manifest;
                config.DllHash = dllHash; config.ManifestHash = manifestHash;
                SaveConfiguration();
                startService(package.Directory);
                updater.Activate(package);
            }
            catch (Exception activationFailure)
            {
                Exception cleanupFailure = null, recoveryFailure = null;
                try { stopService(); }
                catch (Exception error) { cleanupFailure = error; }
                config.DllPath = oldDll; config.ManifestPath = oldManifest;
                config.DllHash = oldDllHash; config.ManifestHash = oldManifestHash;
                // Restore selection even if stopping the failed service fails.
                try { SaveConfiguration(); startService(Path.GetDirectoryName(oldDll)); }
                catch (Exception error) { recoveryFailure = error; }
                if (cleanupFailure != null || recoveryFailure != null)
                    throw new IOException("Update failed: " + activationFailure.Message +
                        (cleanupFailure == null ? "" : "; cleanup: " + cleanupFailure.Message) +
                        (recoveryFailure == null ? "" : "; recovery: " + recoveryFailure.Message), activationFailure);
                throw;
            }
        }
        private void SaveConfiguration()
        {
            string temporary = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { Files.Save(config, temporary); File.Replace(temporary, configPath, null); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private sealed class OwnedProcess { public int Pid = 0; public long Creation = 0; }
        private void StopOwnedService()
        {
            string recordPath = Path.Combine(config.RuntimeDirectory, "service-process.json");
            if (!File.Exists(recordPath)) throw new IOException("Owned verification service record is missing");
            OwnedProcess record = Files.Json.Deserialize<OwnedProcess>(File.ReadAllText(recordPath));
            Process service;
            try { service = Process.GetProcessById(record.Pid); }
            catch (ArgumentException) { return; }
            using (service)
            {
                if (service.HasExited) return;
                string expected = Path.Combine(Path.GetDirectoryName(config.DllPath), "AdmissionService.exe");
                if (Files.Creation(service) != record.Creation || !string.Equals(service.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Verification service identity changed");
                service.Kill();
                if (!service.WaitForExit(5000)) throw new IOException("Verification service did not stop");
            }
        }
    }
}
