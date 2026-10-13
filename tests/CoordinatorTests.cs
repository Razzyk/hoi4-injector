using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using HOI4Admission;

internal static class CoordinatorTests
{
    private static string root;
    private static int cases, assertions;

    private sealed class EqualReleaseTransport : IModuleTransport
    {
        public int Calls;
        public byte[] Get(Uri uri, int maximumBytes, bool metadata)
        {
            Interlocked.Increment(ref Calls);
            if (!metadata) throw new InvalidOperationException("Equal release must not download an archive");
            return Encoding.UTF8.GetBytes("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v0.3.0\",\"assets\":[]}");
        }
    }

    private sealed class Fixture
    {
        public readonly string Root, ConfigPath, OldDirectory, OldDll, OldManifest, OldDllHash, OldManifestHash;
        public readonly Configuration Config;
        public readonly ModuleUpdater Updater;
        public readonly ModulePackage Current;
        public readonly EqualReleaseTransport Transport;
        public readonly string PointerBefore, ConfigBefore;

        public Fixture(string name)
        {
            Root = Path.Combine(root, name);
            string bundle = Path.Combine(Root, "bundle");
            OldDirectory = Path.Combine(Root, "old-module");
            WritePackage(bundle, "0.3.0", "current");
            WritePackage(OldDirectory, "0.2.0", "legacy");
            Transport = new EqualReleaseTransport();
            Updater = new ModuleUpdater(Path.Combine(Root, "data"), bundle, null, Transport);
            Current = Updater.Current();
            string runtime = Path.Combine(Root, "runtime");
            Directory.CreateDirectory(runtime);
            string game = Path.Combine(Root, "hoi4.exe");
            File.WriteAllText(game, "Harmless fixture file; never executed.");
            OldDll = Path.Combine(OldDirectory, "HOI4Admission.dll");
            OldManifest = Path.Combine(OldDirectory, "mod-manifest.json");
            OldDllHash = Files.Hash(OldDll); OldManifestHash = Files.Hash(OldManifest);
            Config = new Configuration {
                SessionId = "fixture-session", PipeName = "fixture-unused-pipe", GamePath = game,
                DllPath = OldDll, DllHash = OldDllHash, ManifestPath = OldManifest,
                ManifestHash = OldManifestHash, RuntimeDirectory = runtime,
                Players = new[] {
                    new Player { Id = "player-one", Secret = new string('a', 64), Allowed = true },
                    new Player { Id = "player-two", Secret = new string('b', 64), Allowed = false }
                },
                Exclusions = new[] { new Exclusion { Pid = 123, Creation = 456 } }
            };
            ConfigPath = Path.Combine(runtime, "session.json");
            Files.Save(Config, ConfigPath);
            ConfigBefore = File.ReadAllText(ConfigPath);
            PointerBefore = File.ReadAllText(Path.Combine(Root, "data", "current.json"));
        }
        public void AssertOld(string context)
        {
            Assert(Config.DllPath == OldDll && Config.ManifestPath == OldManifest, context + ": in-memory paths restored");
            Assert(Config.DllHash == OldDllHash && Config.ManifestHash == OldManifestHash, context + ": in-memory hashes restored");
            Configuration saved = Files.Load(ConfigPath);
            Assert(saved.DllPath == OldDll && saved.ManifestPath == OldManifest, context + ": saved paths restored");
            Assert(saved.DllHash == OldDllHash && saved.ManifestHash == OldManifestHash, context + ": saved hashes restored");
            Assert(File.ReadAllText(ConfigPath) == ConfigBefore, context + ": complete saved configuration preserved");
            Assert(File.ReadAllText(Path.Combine(Root, "data", "current.json")) == PointerBefore, context + ": current pointer preserved");
            Assert(Directory.GetFiles(Path.GetDirectoryName(ConfigPath), "*.tmp").Length == 0, context + ": no temporary configuration leaked");
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "H4Coordinator", Guid.NewGuid().ToString("N")) : args[0]);
            Directory.CreateDirectory(root);
            Test("existing session migration waits for game exit and needs no newer release", delegate {
                var f = new Fixture("defer");
                bool running = true;
                int stops = 0;
                var starts = new List<string>();
                Player[] players = f.Config.Players;
                var coordinator = new UpdateCoordinator(f.Config, f.ConfigPath, f.Updater,
                    () => running, () => { stops++; }, directory => {
                        starts.Add(directory);
                        Assert(f.Config.DllPath == Path.Combine(directory, "HOI4Admission.dll"), "new configuration visible before starting service");
                        Assert(Files.Load(f.ConfigPath).DllPath == f.Config.DllPath, "new selection saved before starting service");
                    });
                Assert(coordinator.Status.Contains("migration pending"), "constructor detects configuration/current module mismatch");
                Assert(f.Updater.CheckAndStage() == null, "latest release equals already installed current module");
                coordinator.Tick();
                Assert(stops == 0 && starts.Count == 0, "running game prevents service changes");
                Assert(coordinator.Status.Contains("close HOI4"), "deferred migration explains required game exit");
                f.AssertOld("deferred migration");
                running = false;
                coordinator.Tick();
                Assert(stops == 1 && starts.SequenceEqual(new[] { f.Current.Directory }), "game exit activates the current module once");
                string expectedDll = Path.Combine(f.Current.Directory, "HOI4Admission.dll");
                string expectedManifest = Path.Combine(f.Current.Directory, "mod-manifest.json");
                Assert(f.Config.DllPath == expectedDll && f.Config.ManifestPath == expectedManifest, "configuration paths migrate to installed current package");
                Assert(f.Config.DllHash == Files.Hash(expectedDll) && f.Config.ManifestHash == Files.Hash(expectedManifest), "configuration hashes migrate with paths");
                Configuration saved = Files.Load(f.ConfigPath);
                Assert(saved.DllPath == expectedDll && saved.ManifestPath == expectedManifest && saved.DllHash == f.Config.DllHash && saved.ManifestHash == f.Config.ManifestHash, "migration is persisted");
                Assert(object.ReferenceEquals(f.Config.Players, players) && saved.Players.Select(p => p.Id + p.Secret + p.Allowed).SequenceEqual(players.Select(p => p.Id + p.Secret + p.Allowed)), "player identities, credentials and permissions preserved");
                Assert(saved.SessionId == "fixture-session" && saved.PipeName == "fixture-unused-pipe" && saved.GamePath == f.Config.GamePath, "session, pipe and game identity preserved");
                Assert(saved.Exclusions.Length == 1 && saved.Exclusions[0].Pid == 123 && saved.Exclusions[0].Creation == 456, "plain test exclusion preserved");
                Assert(f.Updater.Current().Version == "0.3.0" && File.ReadAllText(Path.Combine(f.Root, "data", "current.json")) == f.PointerBefore, "migration preserves validated current version and pointer");
                SettleCheck(coordinator);
            });
            Test("start failure plus cleanup failure still restores configuration and attempts recovery", delegate {
                var f = new Fixture("rollback");
                int stops = 0;
                var starts = new List<string>();
                var coordinator = new UpdateCoordinator(f.Config, f.ConfigPath, f.Updater,
                    () => false, () => { if (++stops == 2) throw new IOException("synthetic cleanup failure"); }, directory => {
                        starts.Add(directory);
                        if (directory == f.Current.Directory) throw new IOException("synthetic start failure");
                    });
                coordinator.Tick();
                Assert(stops == 2, "failed new service receives cleanup attempt");
                Assert(starts.SequenceEqual(new[] { f.Current.Directory, f.OldDirectory }), "old service recovery attempted after cleanup failure");
                f.AssertOld("failed activation");
                Assert(coordinator.Status.Contains("synthetic start failure") && coordinator.Status.Contains("synthetic cleanup failure"), "status retains original and cleanup errors");
                Assert(coordinator.Status.Contains("retained for retry"), "failed module remains pending for retry");
            });
            Test("initial service ownership failure preserves selection without starting a service", delegate {
                var f = new Fixture("stop-fail");
                int stops = 0, starts = 0;
                var coordinator = new UpdateCoordinator(f.Config, f.ConfigPath, f.Updater,
                    () => false, () => { stops++; throw new IOException("synthetic ownership mismatch"); }, directory => { starts++; });
                coordinator.Tick();
                Assert(stops == 1 && starts == 0, "initial stop failure prevents activation and recovery start");
                f.AssertOld("initial stop failure");
                Assert(coordinator.Status.Contains("synthetic ownership mismatch"), "ownership failure is visible");
            });
            Test("game appearing during validation defers activation before service or config changes", delegate {
                var f = new Fixture("race");
                int checks = 0, stops = 0, starts = 0;
                var coordinator = new UpdateCoordinator(f.Config, f.ConfigPath, f.Updater,
                    () => ++checks >= 3, () => { stops++; }, directory => { starts++; });
                coordinator.Tick();
                Assert(checks == 3 && stops == 0 && starts == 0, "post-validation game check prevents activation");
                f.AssertOld("game-start race");
                Assert(coordinator.Status.Contains("HOI4 started"), "raced game start is explained");
            });
            Test("failed recovery is reported after configuration has been restored", delegate {
                var f = new Fixture("recover-fail");
                var starts = new List<string>();
                var coordinator = new UpdateCoordinator(f.Config, f.ConfigPath, f.Updater,
                    () => false, () => { }, directory => {
                        starts.Add(directory);
                        throw new IOException(directory == f.Current.Directory ? "synthetic new start failure" : "synthetic recovery failure");
                    });
                coordinator.Tick();
                f.AssertOld("recovery failure");
                Assert(starts.SequenceEqual(new[] { f.Current.Directory, f.OldDirectory }), "both new and old service starts attempted");
                Assert(coordinator.Status.Contains("synthetic new start failure") && coordinator.Status.Contains("synthetic recovery failure"), "activation and recovery errors retained");
            });
            Test("autostart preserves quoted custom data root without constructing a UI", delegate {
                // Invoke only the pure command getter. No Form constructor, window,
                // application message loop, registry write or actual service runs.
                var form = (GuardForm)FormatterServices.GetUninitializedObject(typeof(GuardForm));
                GC.SuppressFinalize(form);
                string customRoot = Path.Combine(root, "custom data root");
                string config = Path.Combine(root, "custom session", "session.json");
                typeof(GuardForm).GetField("dataRoot", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, customRoot);
                typeof(GuardForm).GetField("configPath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, config);
                string command = (string)typeof(GuardForm).GetProperty("StartupCommand", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form, null);
                Assert(command.Contains(" --data-dir \"" + customRoot + "\""), "custom updater storage included and quoted");
                Assert(command.Contains(" --config \"" + config + "\""), "custom configuration included and quoted");
                Assert(command.EndsWith(" --minimized", StringComparison.Ordinal), "autostart stays minimized");
            });
            Console.WriteLine("Coordinator: " + cases + " cases, " + assertions + " assertions passed. No game or service process launched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void SettleCheck(UpdateCoordinator coordinator)
    {
        var timer = Stopwatch.StartNew();
        while (coordinator.Status == "Checking DLL releases" && timer.ElapsedMilliseconds < 3000)
        { Thread.Sleep(10); coordinator.Tick(); }
        Assert(coordinator.Status == "DLL is up to date", "migration settles against equal latest release");
    }
    private static void WritePackage(string directory, string version, string marker)
    {
        Directory.CreateDirectory(directory);
        string[] names = { "HOI4Admission.dll", "AdmissionService.exe", "mod-manifest.json" };
        foreach (string name in names) File.WriteAllText(Path.Combine(directory, name), marker + ":" + name, new UTF8Encoding(false));
        var descriptor = new {
            schema = 1, version = version, protocol = 2, minimumInjectorVersion = "0.4.0",
            gameVersion = ModuleUpdater.SupportedGameVersion, gameSha256 = ModuleUpdater.SupportedGameSha256,
            files = names.Select(name => new { path = name, sha256 = Files.Hash(Path.Combine(directory, name)) }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, "module-release.json"), Files.Json.Serialize(descriptor), new UTF8Encoding(false));
    }
    private static void Test(string name, Action body)
    { body(); cases++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool condition, string message)
    { assertions++; if (!condition) throw new Exception("Assertion failed: " + message); }
}
