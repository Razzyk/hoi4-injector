using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace HOI4Admission
{
    public sealed class ManagedGame
    {
        public int Pid;
        public long Creation;
        public string Player = "", State = "Detected", Detail = "Startup delay";
        public DateTime DetectedUtc = DateTime.UtcNow;
        public bool Attempted;
    }
    public sealed class ManagerEngine
    {
        private readonly Configuration config;
        private readonly string binaries;
        private readonly Dictionary<string, ManagedGame> games = new Dictionary<string, ManagedGame>();
        public bool Enabled = true;
        public string Error = "";
        public ManagerEngine(Configuration c, string binaryDirectory) { config = c; binaries = binaryDirectory; }
        private void Log(string text) { File.AppendAllText(Path.Combine(config.RuntimeDirectory, "manager.log"), DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine); }
        public ManagedGame[] Tick()
        {
            if (!Enabled) return games.Values.ToArray();
            var seen = new HashSet<string>();
            Process[] processes = Process.GetProcessesByName("hoi4");
            try
            {
                foreach (Process p in processes)
                {
                    try
                    {
                        if (p.HasExited || !string.Equals(p.MainModule.FileName, config.GamePath, StringComparison.OrdinalIgnoreCase)) continue;
                        long creation = Files.Creation(p);
                        string key = Files.Identity(p.Id, creation);
                        seen.Add(key);
                        ManagedGame game;
                        if (!games.TryGetValue(key, out game))
                        {
                            game = new ManagedGame { Pid = p.Id, Creation = creation };
                            games.Add(key, game);
                            Log("GAME_DETECTED " + key);
                        }
                        if (config.Exclusions.Any(e => e.Pid == p.Id && e.Creation == creation))
                        { game.State = "Plain - injection excluded"; game.Detail = "Explicitly excluded test instance"; continue; }
                        if (game.Attempted) continue;
                        if ((DateTime.UtcNow - game.DetectedUtc).TotalMilliseconds < config.StartupDelayMs) continue;
                        if (!p.Responding) { game.State = "Waiting for response"; continue; }
                        if (p.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, "HOI4Admission.dll", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(m.FileName, config.DllPath, StringComparison.OrdinalIgnoreCase)))
                        { game.State = "Different DLL build - restart game"; game.Detail = "Close this game and launch a fresh copy for lobby enforcement"; game.Attempted = true; continue; }
                        Player player = config.Players.FirstOrDefault(slot => slot.Allowed && !games.Values.Any(g => g.Player == slot.Id));
                        if (player == null) { game.State = "No enrolled player slot"; game.Detail = "No injection performed"; continue; }
                        game.Player = player.Id;
                        game.Attempted = true;
                        Load(game, player);
                    }
                    catch (InvalidOperationException) { /* process exited during observation */ }
                    catch (Exception e) { Error = "Could not inspect HOI4: " + e.Message; return games.Values.ToArray(); }
                }
                foreach (string departed in games.Keys.Where(k => !seen.Contains(k)).ToArray()) { Log("GAME_EXITED " + departed); games.Remove(departed); }
                ServiceSnapshot snapshot = Files.Json.Deserialize<ServiceSnapshot>(Wire.Control(config, "STATUS"));
                foreach (ManagedGame game in games.Values.Where(g => g.Attempted && g.State != "Load failed" && g.State != "Different DLL build - restart game"))
                {
                    ClientRecord client = snapshot.Clients.FirstOrDefault(c => c.Pid == game.Pid && c.Creation == game.Creation);
                    if (client != null) game.Player = client.Player;
                    game.State = client != null && client.Eligible ? "Verified - eligible" : "Loaded - awaiting verification";
                    game.Detail = client != null ? client.Detail : "DLL worker connects to the local service";
                }
                Error = "";
            }
            catch (Exception e) { Error = "Verification service unavailable: " + e.Message; }
            finally
            {
                foreach (Process p in processes)
                {
                    ManagedGame game = games.Values.FirstOrDefault(g => g.Pid == p.Id);
                    if (game != null) WindowLabels.Refresh(p, game, config, Error.Length == 0);
                    p.Dispose();
                }
            }
            return games.Values.OrderBy(g => g.Pid).ToArray();
        }
        private void Load(ManagedGame game, Player player)
        {
            string ini = Path.Combine(config.RuntimeDirectory, "bootstrap-" + game.Pid + "-" + game.Creation + ".ini");
            string dllLog = Path.Combine(config.RuntimeDirectory, "dll-" + game.Pid + ".log");
            string content = "[admission]\r\nSession=" + config.SessionId + "\r\nPlayer=" + player.Id + "\r\nSecret=" + player.Secret + "\r\nDllHash=" + config.DllHash + "\r\nPipe=\\\\.\\pipe\\" + config.PipeName + "\r\nManifest=" + config.ManifestPath + "\r\nLog=" + dllLog + "\r\n";
            File.WriteAllText(ini, content, Encoding.Unicode);
            var start = new ProcessStartInfo(Path.Combine(binaries, "AdmissionLoader.exe"), game.Pid + " " + game.Creation + " \"" + config.DllPath + "\" \"" + ini + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            try
            {
                using (Process loader = Process.Start(start))
                {
                    string stdout = loader.StandardOutput.ReadToEnd();
                    string stderr = loader.StandardError.ReadToEnd();
                    loader.WaitForExit();
                    game.State = loader.ExitCode == 0 ? "Loaded - awaiting verification" : "Load failed";
                    game.Detail = loader.ExitCode == 0 ? "Native module confirmed; waiting for service handshake" : stderr.Trim();
                    Log("LOADER_RESULT pid=" + game.Pid + " exit=" + loader.ExitCode + " " + stdout.Trim() + " " + stderr.Trim());
                }
            }
            catch (Exception e) { game.State = "Load failed"; game.Detail = e.Message; Log("LOADER_ERROR pid=" + game.Pid + " " + e.Message); }
            finally { File.Delete(ini); }
        }
    }
}
