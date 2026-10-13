using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HOI4Admission
{
    public static class ManagerProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                string binaries = AppDomain.CurrentDomain.BaseDirectory;
                string dataRoot = Path.GetFullPath(Option(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HOI4Injector"));
                Files.MakePrivate(dataRoot);
                ReleaseSource releaseSource = Files.Json.Deserialize<ReleaseSource>(File.ReadAllText(Path.Combine(binaries, "release-source.json")));
                var updater = new ModuleUpdater(dataRoot, Path.Combine(binaries, "bundled-module"), releaseSource.moduleRepository);
                ModulePackage module = updater.Current();
                string configurationPath = Option(args, "--config") ?? Path.Combine(dataRoot, "runtime", "session.json");
                string init = Option(args, "--init");
                string launch = Option(args, "--launch-test");
                if (init != null || launch != null)
                {
                    configurationPath = Initialize(init ?? launch, Option(args, "--game-exe"), module, false);
                    if (init != null) return 0;
                }
                if (!File.Exists(configurationPath)) configurationPath = Initialize(Path.GetDirectoryName(configurationPath), Option(args, "--game-exe"), module, Option(args, "--headless-seconds") == null);
                Configuration config = Files.Load(configurationPath);
                if (Array.IndexOf(args, "--status") >= 0) { Console.WriteLine(Wire.Control(config, "STATUS")); return 0; }
                if (Array.IndexOf(args, "--lobby-events") >= 0)
                {
                    ServiceSnapshot snapshot = Files.Json.Deserialize<ServiceSnapshot>(Wire.Control(config, "STATUS"));
                    var eventGames = new System.Collections.Generic.List<ManagedGame>();
                    foreach (Process game in Process.GetProcessesByName("hoi4"))
                    {
                        using (game)
                        {
                            try
                            {
                                if (game.HasExited || !string.Equals(game.MainModule.FileName, config.GamePath, StringComparison.OrdinalIgnoreCase)) continue;
                                long creation = Files.Creation(game);
                                ClientRecord record = snapshot.Clients.FirstOrDefault(r => r.Pid == game.Id && r.Creation == creation);
                                eventGames.Add(new ManagedGame { Pid = game.Id, Creation = creation, Player = record == null ? "" : record.Player });
                            }
                            catch (InvalidOperationException) { }
                            catch (System.ComponentModel.Win32Exception) { }
                        }
                    }
                    Console.WriteLine(Files.Json.Serialize(LobbyActivity.Read(config, eventGames.ToArray())));
                    return 0;
                }
                string admit = Option(args, "--admit");
                if (admit != null)
                {
                    string player = Option(args, "--player") ?? "player-one";
                    using (Process game = Process.GetProcessById(int.Parse(admit))) Console.WriteLine(Wire.Control(config, "ADMIT|" + config.SessionId + "|" + player + "|" + game.Id + "|" + Files.Creation(game)));
                    return 0;
                }
                EnsureService(config, configurationPath, Path.GetDirectoryName(config.DllPath));
                var updates = new UpdateCoordinator(config, configurationPath, updater);
                if (launch != null) LaunchThree(config, configurationPath);
                string headless = Option(args, "--headless-seconds");
                if (headless != null)
                {
                    int seconds = int.Parse(headless);
                    if (seconds < 1 || seconds > 600) throw new ArgumentException("Headless duration must be between 1 and 600 seconds");
                    var engine = new ManagerEngine(config, binaries);
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed.TotalSeconds < seconds) { updates.Tick(); engine.Tick(); Thread.Sleep(1000); }
                    File.WriteAllText(Path.Combine(config.RuntimeDirectory, "manager-final.json"), Files.Json.Serialize(engine.Tick()));
                    return engine.Error.Length == 0 ? 0 : 1;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool first;
                using (var mutex = new Mutex(true, @"Local\HOI4Admission-" + config.SessionId, out first))
                {
                    if (!first) return 0;
                    try { using (var form = new GuardForm(config, configurationPath, binaries, Array.IndexOf(args, "--minimized") >= 0, updates, releaseSource, dataRoot)) Application.Run(form); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
                if (Array.IndexOf(args, "--headless-seconds") < 0 && Array.IndexOf(args, "--init") < 0 && Array.IndexOf(args, "--status") < 0 && Array.IndexOf(args, "--admit") < 0 && Array.IndexOf(args, "--lobby-events") < 0)
                    MessageBox.Show(e.Message, "HOI4 Injector", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        public static string Option(string[] args, string key)
        {
            int i = Array.IndexOf(args, key);
            if (i < 0) return null;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + key);
            return args[i + 1];
        }
        private static string Initialize(string directory, string game, ModulePackage module, bool interactive)
        {
            directory = Path.GetFullPath(directory);
            Files.MakePrivate(directory);
            string path = Path.Combine(directory, "session.json");
            if (File.Exists(path)) return path;
            game = game ?? GameDiscovery.Find(interactive);
            var c = new Configuration {
                SessionId = Guid.NewGuid().ToString("N"), PipeName = "HOI4Admission-" + Guid.NewGuid().ToString("N"),
                GamePath = Path.GetFullPath(game), DllPath = Path.Combine(module.Directory, "HOI4Admission.dll"), ManifestPath = Path.Combine(module.Directory, "mod-manifest.json"),
                RuntimeDirectory = directory, Players = new[] { new Player { Id = "player-one", Secret = Files.RandomHex() }, new Player { Id = "player-two", Secret = Files.RandomHex() } }
            };
            c.DllHash = Files.Hash(c.DllPath); c.ManifestHash = Files.Hash(c.ManifestPath);
            if (Files.Hash(c.GamePath) != c.GameHash) throw new InvalidOperationException("The installed game does not match HOI4 1.19.3.0");
            Files.Save(c, path);
            return path;
        }
        public static void EnsureService(Configuration c, string configPath, string binaries)
        {
            try { Wire.Control(c, "STATUS"); return; } catch (Exception) { }
            // Shell execution detaches standard handles even on the Framework
            // CreateProcess implementation, which inherits unrelated pipe handles.
            var start = new ProcessStartInfo(Path.Combine(binaries, "AdmissionService.exe"), "\"" + Path.GetFullPath(configPath) + "\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            Process service = Process.Start(start);
            File.WriteAllText(Path.Combine(c.RuntimeDirectory, "service-process.json"), Files.Json.Serialize(new { Pid = service.Id, Creation = Files.Creation(service) }));
            try
            {
                for (int i = 0; i < 30; ++i)
                {
                    if (service.HasExited) throw new InvalidOperationException("Local verification service exited during startup");
                    try { Wire.Control(c, "STATUS"); return; } catch (Exception) { Thread.Sleep(100); }
                }
                throw new TimeoutException("Local verification service did not start");
            }
            finally { service.Dispose(); }
        }
        public static Process LaunchGame(Configuration c, string name)
        {
            string profile = Path.Combine(c.RuntimeDirectory, "profiles", name.Replace('-', '_'));
            // This game build tokenizes its command line on '-'. Even a quoted
            // profile path with hyphens is split and silently falls back to the
            // ordinary user directory. Keep game-only paths free of hyphens.
            if (profile.IndexOf('-') >= 0)
                profile = Path.Combine(Path.GetTempPath(), "HOI4Admission", c.SessionId, name.Replace('-', '_'));
            if (profile.IndexOf('-') >= 0) throw new InvalidOperationException("HOI4 needs a test-profile path without hyphens; use a runtime directory without '-' characters.");
            Directory.CreateDirectory(profile);
            File.AppendAllText(Path.Combine(c.RuntimeDirectory, "profile-paths.jsonl"), Files.Json.Serialize(new { Name = name, Profile = profile }) + Environment.NewLine);
            File.WriteAllText(Path.Combine(profile, "dlc_load.json"), "{\"disabled_dlcs\":[],\"enabled_mods\":[]}");
            File.WriteAllText(Path.Combine(profile, "settings.txt"), "graphics={\n size={ x=960 y=540 }\n fullScreen=no\n borderless=no\n multisampling=0\n shadowSize=1024\n maxanisotropy=0\n vsync=yes\n}\n master_volume=0\n");
            File.WriteAllText(Path.Combine(profile, "pdx_settings.json"), "{\"display_mode\":\"windowed\",\"windowed_resolution\":{\"width\":960,\"height\":540},\"vsync\":true}");
            return Process.Start(new ProcessStartInfo(c.GamePath, "-gdpr-compliant -userdir=\"" + profile.Replace('\\', '/') + "\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal, WorkingDirectory = Path.GetDirectoryName(c.GamePath) });
        }
        private static void LaunchThree(Configuration c, string configPath)
        {
            string summaryPath = Path.Combine(c.RuntimeDirectory, "launched-games.json");
            if (File.Exists(summaryPath)) throw new InvalidOperationException("This test session already has a launch record. Use a new test directory to launch another group.");
            using (Process first = LaunchGame(c, "injected-one"))
            using (Process second = LaunchGame(c, "injected-two"))
            using (Process plain = LaunchGame(c, "plain"))
            {
                var exclusion = new Exclusion { Pid = plain.Id, Creation = Files.Creation(plain) };
                c.Exclusions = c.Exclusions.Concat(new[] { exclusion }).ToArray();
                Files.Save(c, configPath);
                File.WriteAllText(summaryPath, Files.Json.Serialize(new[] {
                        new { Name = "injected-one", Pid = first.Id, Creation = Files.Creation(first), Inject = true },
                        new { Name = "injected-two", Pid = second.Id, Creation = Files.Creation(second), Inject = true },
                        new { Name = "plain", Pid = plain.Id, Creation = exclusion.Creation, Inject = false }
                    }));
            }
        }
    }
}
