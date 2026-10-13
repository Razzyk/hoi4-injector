using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using HOI4Admission;

internal sealed class UpdaterTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string root;
    private static int passed;
    private static readonly string[] PayloadNames = { "HOI4Admission.dll", "AdmissionService.exe", "mod-manifest.json" };

    private sealed class Transport : IModuleTransport
    {
        public byte[] Metadata, Archive;
        public bool Offline;
        public int Calls, Downloads;
        public byte[] Get(Uri uri, int maximumBytes, bool metadata)
        {
            Calls++;
            if (Offline) throw new WebException("Fixture offline");
            if (metadata)
            {
                Assert(uri.AbsoluteUri == "https://api.github.com/repos/Razzyk/hoi4-admission-mod/releases/latest", "Configured release endpoint");
                return Metadata;
            }
            Downloads++;
            Assert(uri.AbsoluteUri == "https://github.com/Razzyk/hoi4-admission-mod/releases/download/v0.4.0/HOI4-Admission-Mod-win-x64.zip", "Pinned release asset URL");
            Assert(maximumBytes == ModuleUpdater.MaximumPackageBytes, "Download cap reaches transport");
            return Archive;
        }
    }
    private sealed class Fixture
    {
        public string Root, Bundle, Data;
        public Transport Transport;
        public ModuleUpdater Updater;
        public Fixture(string name)
        {
            Root = Path.Combine(root, name);
            Bundle = Path.Combine(Root, "bundle");
            Data = Path.Combine(Root, "data");
            Directory.CreateDirectory(Bundle);
            WritePackage(Bundle, "0.3.0", "bundled");
            Transport = new Transport();
            Transport.Archive = Archive(Package("0.4.0", "updated"));
            Transport.Metadata = Release(Transport.Archive, "v0.4.0", null);
            Updater = new ModuleUpdater(Data, Bundle, null, Transport);
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "HOI4UpdaterTests", Guid.NewGuid().ToString("N")) : args[0]);
            Directory.CreateDirectory(root);
            Test("offline first run installs and validates bundled module without HTTP", delegate
            {
                var f = new Fixture("offline-bundle"); f.Transport.Offline = true;
                ModulePackage current = f.Updater.Current();
                Assert(current.Version == "0.3.0" && current.Protocol == 2 && File.Exists(Path.Combine(current.Directory, "HOI4Admission.dll")), "Bundled package installed");
                Assert(f.Transport.Calls == 0, "Current is offline");
                Assert(new ModuleUpdater(f.Data, Path.Combine(f.Root, "missing-bundle"), null, f.Transport).Current().Version == "0.3.0", "Installed pointer works without bundle");
            });
            Test("Windows UTF-8 BOM descriptors are accepted without weakening JSON validation", delegate
            {
                var f = new Fixture("bom-bundle");
                string path = Path.Combine(f.Bundle, "module-release.json");
                File.WriteAllText(path, File.ReadAllText(path), new UTF8Encoding(true));
                Assert(f.Updater.Current().Version == "0.3.0", "UTF-8 BOM supported");
            });
            Test("valid update remains staged until explicit atomic activation", delegate
            {
                var f = new Fixture("stage-activate");
                ModulePackage current = f.Updater.Current();
                string before = File.ReadAllText(Path.Combine(f.Data, "current.json"));
                ModulePackage candidate = f.Updater.CheckAndStage();
                Assert(candidate != null && candidate.Version == "0.4.0", "New stable version staged");
                Assert(f.Updater.Current().Version == "0.3.0" && File.ReadAllText(Path.Combine(f.Data, "current.json")) == before, "Staging preserves active pointer");
                Assert(ModuleUpdater.Hash(File.ReadAllBytes(Path.Combine(current.Directory, "HOI4Admission.dll"))) == ModuleUpdater.Hash(Encoding.UTF8.GetBytes("bundled-HOI4Admission.dll")), "Previous version not modified");
                f.Updater.Activate(candidate);
                Assert(f.Updater.Current().Version == "0.4.0", "Activation selects new version");
                Assert(f.Updater.CheckAndStage() == null, "Equal version is no update");
            });
            Test("network outage preserves installed pointer and permits offline reuse", delegate
            {
                var f = new Fixture("offline-update"); f.Updater.Current(); f.Transport.Offline = true;
                Refused(f, "could not be checked");
                Assert(f.Updater.Current().Version == "0.3.0", "Offline current remains usable");
            });
            Test("GitHub digest mismatch refuses package", delegate
            {
                var f = new Fixture("asset-bad-digest");
                f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { Asset(r)["digest"] = "sha256:" + new string('0', 64); });
                Refused(f, "GitHub SHA-256");
            });
            Test("missing GitHub digest fails closed", delegate
            {
                var f = new Fixture("asset-missing-digest");
                f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { Asset(r).Remove("digest"); });
                Refused(f, "digest"); Assert(f.Transport.Downloads == 0, "Missing digest refused before download");
            });
            Test("release origin cannot be replaced with a different repository", delegate
            {
                var f = new Fixture("wrong-origin");
                f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { Asset(r)["browser_download_url"] = "https://github.com/other/hoi4-admission-mod/releases/download/v0.4.0/" + ModuleUpdater.AssetName; });
                Refused(f, "outside"); Assert(f.Transport.Downloads == 0, "Foreign URL never fetched");
            });
            Test("HTTP and encoded archive paths are refused before download", delegate
            {
                foreach (string url in new[] {
                    "http://github.com/Razzyk/hoi4-admission-mod/releases/download/v0.4.0/" + ModuleUpdater.AssetName,
                    "https://github.com/Razzyk/hoi4-admission-mod/releases/download/v0.4.0/%48OI4-Admission-Mod-win-x64.zip",
                    "https://github.com/Razzyk/hoi4-admission-mod/releases/download/v0.4.0/" + ModuleUpdater.AssetName + "?other=1",
                    "https://other@github.com/Razzyk/hoi4-admission-mod/releases/download/v0.4.0/" + ModuleUpdater.AssetName,
                    "https://github.com:444/Razzyk/hoi4-admission-mod/releases/download/v0.4.0/" + ModuleUpdater.AssetName })
                {
                    var f = new Fixture("unsafe-url-" + Guid.NewGuid().ToString("N"));
                    f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { Asset(r)["browser_download_url"] = url; });
                    Refused(f, "outside"); Assert(f.Transport.Downloads == 0, "Unsafe URL never fetched");
                }
            });
            Test("draft and prerelease metadata cannot install", delegate
            {
                foreach (string field in new[] { "draft", "prerelease" })
                {
                    var f = new Fixture("unstable-" + field);
                    f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { r[field] = true; });
                    Refused(f, "stable");
                }
            });
            Test("downgrade is refused without asset download", delegate
            {
                var f = new Fixture("downgrade"); f.Transport.Metadata = Release(f.Transport.Archive, "v0.2.0", null);
                Refused(f, "downgrade"); Assert(f.Transport.Downloads == 0, "Downgrade does not download");
            });
            Test("semantic version prerelease, leading zero and overflow are refused", delegate
            {
                foreach (string tag in new[] { "v0.4.0-beta", "v00.4.0", "v2147483648.4.0", "v0.4", "v../0.4.0" })
                {
                    var f = new Fixture("semver-" + Guid.NewGuid().ToString("N")); f.Transport.Metadata = Release(f.Transport.Archive, tag, null);
                    Refused(f, "version");
                }
                Assert(ModuleUpdater.CompareVersions("0.10.0", "0.9.9") > 0, "Numeric semantic version ordering");
            });
            Test("release and package descriptor version must match", delegate
            {
                var f = new Fixture("version-mismatch"); SetArchive(f, Archive(Package("0.5.0", "other")));
                Refused(f, "version does not match");
            });
            Test("each payload file SHA-256 is required and checked", delegate
            {
                foreach (string name in PayloadNames)
                {
                    var f = new Fixture("file-hash-" + name.Replace('.', '-'));
                    Dictionary<string, byte[]> package = Package("0.4.0", "updated"); package[name] = Encoding.UTF8.GetBytes("modified after descriptor");
                    SetArchive(f, Archive(package)); Refused(f, "integrity check");
                }
            });
            Test("missing payload and descriptor hashes are refused", delegate
            {
                var f = new Fixture("missing-file"); Dictionary<string, byte[]> package = Package("0.4.0", "updated"); package.Remove("AdmissionService.exe");
                SetArchive(f, Archive(package)); Refused(f, "exactly four");
                f = new Fixture("missing-hash"); package = Package("0.4.0", "updated");
                MutateDescriptor(package, delegate(Dictionary<string, object> d) { var hashes = (System.Collections.IList)d["files"]; ((Dictionary<string, object>)hashes[0]).Remove("sha256"); });
                SetArchive(f, Archive(package)); Refused(f, "missing or unsupported");
            });
            Test("ZIP traversal, subdirectories and duplicate names are refused", delegate
            {
                foreach (string path in new[] { "../HOI4Admission.dll", "sub/HOI4Admission.dll", "HOI4ADMISSION.dll", "C:\\HOI4Admission.dll", "HOI4Admission.dll/" })
                {
                    var f = new Fixture("unsafe-path-" + Guid.NewGuid().ToString("N")); Dictionary<string, byte[]> package = Package("0.4.0", "updated"); byte[] dll = package["HOI4Admission.dll"]; package.Remove("HOI4Admission.dll"); package[path] = dll;
                    SetArchive(f, Archive(package)); Refused(f, "unsafe archive path");
                    Assert(!File.Exists(Path.Combine(f.Root, "HOI4Admission.dll")), "No traversal write");
                }
                var duplicate = new Fixture("duplicate-entry");
                SetArchive(duplicate, ArchiveWithDuplicate(Package("0.4.0", "updated"))); Refused(duplicate, "duplicate");
            });
            Test("ZIP symlinks and Windows reparse attributes are refused", delegate
            {
                foreach (int attributes in new[] { unchecked((int)0xA1FF0000), (int)FileAttributes.ReparsePoint })
                {
                    var f = new Fixture("symlink-" + Guid.NewGuid().ToString("N"));
                    SetArchive(f, Archive(Package("0.4.0", "updated"), attributes)); Refused(f, "Links");
                }
            });
            Test("asset size, actual download size and expanded size caps are enforced", delegate
            {
                var f = new Fixture("oversized-asset");
                f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { Asset(r)["size"] = ModuleUpdater.MaximumPackageBytes + 1; });
                Refused(f, "50 MB"); Assert(f.Transport.Downloads == 0, "Oversized asset refused before download");
                f = new Fixture("wrong-size");
                f.Transport.Metadata = Release(f.Transport.Archive, "v0.4.0", delegate(Dictionary<string, object> r) { Asset(r)["size"] = f.Transport.Archive.Length + 1; });
                Refused(f, "size does not match");
                f = new Fixture("expanded-size"); SetArchive(f, OversizedArchive()); Refused(f, "size limit");
            });
            Test("protocol, game hash and minimum injector compatibility are checked", delegate
            {
                foreach (string field in new[] { "protocol", "gameVersion", "gameSha256", "minimumInjectorVersion" })
                {
                    var f = new Fixture("compat-" + field); Dictionary<string, byte[]> package = Package("0.4.0", "updated");
                    MutateDescriptor(package, delegate(Dictionary<string, object> d) { d[field] = field == "protocol" ? (object)3 : field == "gameSha256" ? new string('0', 64) : field == "gameVersion" ? "1.20.0.0" : "0.5.0"; });
                    SetArchive(f, Archive(package)); Refused(f, field == "minimumInjectorVersion" ? "newer injector" : "incompatible");
                }
            });
            Test("duplicate JSON descriptor keys fail closed", delegate
            {
                var f = new Fixture("duplicate-json"); Dictionary<string, byte[]> package = Package("0.4.0", "updated");
                string descriptor = Encoding.UTF8.GetString(package["module-release.json"]);
                package["module-release.json"] = Encoding.UTF8.GetBytes(descriptor.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"));
                SetArchive(f, Archive(package)); Refused(f, "duplicate fields");
            });
            Test("immutable existing version cannot be overwritten", delegate
            {
                var f = new Fixture("immutable-version"); f.Updater.Current();
                string existing = Path.Combine(f.Data, "modules", "0.4.0"); Directory.CreateDirectory(existing); WritePackage(existing, "0.4.0", "other-valid-content");
                string before = ModuleUpdater.Hash(File.ReadAllBytes(Path.Combine(existing, "HOI4Admission.dll")));
                Refused(f, "different package");
                Assert(ModuleUpdater.Hash(File.ReadAllBytes(Path.Combine(existing, "HOI4Admission.dll"))) == before, "Existing payload remains immutable");
            });
            Test("activation rejects external, altered and downgraded candidates", delegate
            {
                var f = new Fixture("activate-validation"); f.Updater.Current(); ModulePackage next = f.Updater.CheckAndStage();
                string validDirectory = next.Directory;
                next.Directory = f.Bundle; ExpectFailure(delegate { f.Updater.Activate(next); }, "outside"); next.Directory = validDirectory;
                next.Protocol = 99; ExpectFailure(delegate { f.Updater.Activate(next); }, "descriptor changed"); next.Protocol = 2;
                f.Updater.Activate(next);
                ModulePackage old = new ModulePackage { Version = "0.3.0", Directory = Path.Combine(f.Data, "modules", "0.3.0"), Protocol = 2, GameVersion = ModuleUpdater.SupportedGameVersion, GameSha256 = ModuleUpdater.SupportedGameSha256, MinimumInjectorVersion = "0.4.0" };
                ExpectFailure(delegate { f.Updater.Activate(old); }, "older module");
                Assert(f.Updater.Current().Version == "0.4.0", "Invalid activation never changes current");
            });
            Test("corrupt installed package and unsafe current pointer do not silently fallback", delegate
            {
                var f = new Fixture("corrupt-current"); ModulePackage current = f.Updater.Current(); File.AppendAllText(Path.Combine(current.Directory, "AdmissionService.exe"), "modified");
                ExpectFailure(delegate { f.Updater.Current(); }, "integrity"); Assert(f.Transport.Calls == 0, "Corrupt current does not use network");
                f = new Fixture("pointer-traversal"); f.Updater.Current(); File.WriteAllText(Path.Combine(f.Data, "current.json"), "{\"schema\":1,\"version\":\"../bundle\"}");
                ExpectFailure(delegate { f.Updater.Current(); }, "version");
            });
            Console.WriteLine("Updater tests passed: " + passed + " scenarios. Fixtures: " + root);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Assertion failed: " + message); }
    private static void ExpectFailure(Action action, string reason)
    {
        try { action(); } catch (ModuleUpdateException ex) { Assert(ex.Message.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0, "Expected refusal containing '" + reason + "', got: " + ex.Message); return; }
        throw new Exception("Expected update refusal: " + reason);
    }
    private static void Refused(Fixture f, string reason)
    {
        f.Updater.Current(); string before = File.ReadAllText(Path.Combine(f.Data, "current.json"));
        ExpectFailure(delegate { f.Updater.CheckAndStage(); }, reason);
        Assert(File.ReadAllText(Path.Combine(f.Data, "current.json")) == before && f.Updater.Current().Version == "0.3.0", "Refusal preserves installed pointer");
        Assert(!Directory.GetDirectories(Path.Combine(f.Data, "modules")).Any(p => Path.GetFileName(p).StartsWith(".stage-", StringComparison.Ordinal)), "Failed staging directory cleaned up");
    }
    private static Dictionary<string, byte[]> Package(string version, string content)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in PayloadNames) result.Add(name, Encoding.UTF8.GetBytes(content + "-" + name));
        var files = PayloadNames.Select(name => new { path = name, sha256 = ModuleUpdater.Hash(result[name]) }).ToArray();
        result.Add("module-release.json", Encoding.UTF8.GetBytes(Json.Serialize(new { schema = 1, version = version, protocol = 2, minimumInjectorVersion = "0.4.0", gameVersion = ModuleUpdater.SupportedGameVersion, gameSha256 = ModuleUpdater.SupportedGameSha256, files = files })));
        return result;
    }
    private static void WritePackage(string directory, string version, string content) { foreach (var pair in Package(version, content)) File.WriteAllBytes(Path.Combine(directory, pair.Key), pair.Value); }
    private static void MutateDescriptor(Dictionary<string, byte[]> package, Action<Dictionary<string, object>> mutate)
    {
        var descriptor = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(package["module-release.json"]));
        mutate(descriptor); package["module-release.json"] = Encoding.UTF8.GetBytes(Json.Serialize(descriptor));
    }
    private static byte[] Archive(Dictionary<string, byte[]> files, int specialAttributes = 0)
    {
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var pair in files)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(pair.Key);
                    if (pair.Key == "HOI4Admission.dll") entry.ExternalAttributes = specialAttributes;
                    using (Stream stream = entry.Open()) stream.Write(pair.Value, 0, pair.Value.Length);
                }
            }
            return memory.ToArray();
        }
    }
    private static byte[] ArchiveWithDuplicate(Dictionary<string, byte[]> files)
    {
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var pair in files)
                {
                    var entry = archive.CreateEntry(pair.Key == "mod-manifest.json" ? "HOI4Admission.dll" : pair.Key);
                    using (Stream stream = entry.Open()) stream.Write(pair.Value, 0, pair.Value.Length);
                }
            return memory.ToArray();
        }
    }
    private static byte[] OversizedArchive()
    {
        using (var memory = new MemoryStream())
        {
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var pair in Package("0.4.0", "updated"))
                {
                    ZipArchiveEntry entry = archive.CreateEntry(pair.Key);
                    using (Stream stream = entry.Open())
                    {
                        if (pair.Key != "HOI4Admission.dll") stream.Write(pair.Value, 0, pair.Value.Length);
                        else
                        {
                            byte[] zeros = new byte[16384]; int remaining = ModuleUpdater.MaximumPackageBytes + 1;
                            while (remaining > 0) { int count = Math.Min(zeros.Length, remaining); stream.Write(zeros, 0, count); remaining -= count; }
                        }
                    }
                }
            }
            return memory.ToArray();
        }
    }
    private static void SetArchive(Fixture f, byte[] bytes) { f.Transport.Archive = bytes; f.Transport.Metadata = Release(bytes, "v0.4.0", null); }
    private static Dictionary<string, object> Asset(Dictionary<string, object> release) { return (Dictionary<string, object>)((object[])release["assets"])[0]; }
    private static byte[] Release(byte[] archive, string tag, Action<Dictionary<string, object>> mutate)
    {
        var asset = new Dictionary<string, object> { { "name", ModuleUpdater.AssetName }, { "state", "uploaded" }, { "size", archive.Length }, { "digest", "sha256:" + ModuleUpdater.Hash(archive) }, { "browser_download_url", "https://github.com/Razzyk/hoi4-admission-mod/releases/download/" + tag + "/" + ModuleUpdater.AssetName } };
        var release = new Dictionary<string, object> { { "draft", false }, { "prerelease", false }, { "tag_name", tag }, { "assets", new object[] { asset } } };
        if (mutate != null) mutate(release);
        return Encoding.UTF8.GetBytes(Json.Serialize(release));
    }
}
