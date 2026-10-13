using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HOI4Admission
{
    public sealed class ModulePackage
    {
        public string Version, Directory;
        public int Protocol;
        public string GameVersion, GameSha256, MinimumInjectorVersion;
    }

    public sealed class ModuleUpdateException : Exception
    {
        public ModuleUpdateException(string message) : base(message) { }
        public ModuleUpdateException(string message, Exception inner) : base(message, inner) { }
    }

    // The transport seam is internal: production always uses the HTTPS GitHub transport.
    internal interface IModuleTransport { byte[] Get(Uri uri, int maximumBytes, bool metadata); }

    public sealed class ModuleUpdater
    {
        public const string InjectorVersion = "0.4.0";
        public const string DefaultRepository = "Razzyk/hoi4-admission-mod";
        public const string AssetName = "HOI4-Admission-Mod-win-x64.zip";
        public const string SupportedGameVersion = "1.19.3.0";
        public const string SupportedGameSha256 = "7dc947be34970da1e1c787bcddbe7f62b610ff258aebf8f06aa8b348f3a031d5";
        internal const int MaximumPackageBytes = 50 * 1024 * 1024;
        private const int MaximumJsonBytes = 2 * 1024 * 1024;
        private static readonly string[] PackageFiles = { "module-release.json", "HOI4Admission.dll", "AdmissionService.exe", "mod-manifest.json" };
        private readonly string dataRoot, modulesRoot, bundleDirectory, repository;
        private readonly IModuleTransport transport;
        private readonly object sync = new object();

        public ModuleUpdater(string dataRoot, string bundleDirectory, string repository)
            : this(dataRoot, bundleDirectory, repository, new GitHubModuleTransport()) { }

        internal ModuleUpdater(string dataRoot, string bundleDirectory, string repository, IModuleTransport transport)
        {
            if (string.IsNullOrEmpty(dataRoot) || string.IsNullOrEmpty(bundleDirectory) || transport == null) throw new ArgumentException("Module storage and bundled module are required.");
            this.dataRoot = Path.GetFullPath(dataRoot);
            this.bundleDirectory = Path.GetFullPath(bundleDirectory);
            this.modulesRoot = Path.Combine(this.dataRoot, "modules");
            this.repository = string.IsNullOrEmpty(repository) ? DefaultRepository : repository;
            if (!Regex.IsMatch(this.repository, @"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Expected a GitHub owner/repository name.");
            this.transport = transport;
        }

        // No network access here. An invalid installed pointer fails closed rather than
        // silently replacing an installed version with the bundled version.
        public ModulePackage Current()
        {
            lock (sync)
            {
                PrepareStorage();
                string pointer = Path.Combine(dataRoot, "current.json");
                if (File.Exists(pointer))
                {
                    NoReparse(pointer);
                    Dictionary<string, object> value = JsonObject(ReadSmall(pointer, 65536));
                    ExactKeys(value, "schema", "version");
                    if (Integer(value, "schema") != 1) Fail("The installed module pointer has an unsupported schema.");
                    string version = String(value, "version");
                    ParseVersion(version);
                    return ValidateDirectory(Path.Combine(modulesRoot, version), version);
                }
                ModulePackage bundled = ValidateDirectory(bundleDirectory, null);
                string staging = NewStagingDirectory();
                try
                {
                    foreach (string name in PackageFiles) File.Copy(Path.Combine(bundleDirectory, name), Path.Combine(staging, name), false);
                    ModulePackage installed = CommitStaging(staging, bundled.Version);
                    WritePointer(installed.Version);
                    return installed;
                }
                finally { CleanupStaging(staging); }
            }
        }

        // Stages a validated update but never changes current.json, live configuration,
        // the running service, or a DLL already loaded by the game.
        public ModulePackage CheckAndStage()
        {
            lock (sync)
            {
                ModulePackage current = Current();
                try
                {
                    Uri endpoint = new Uri("https://api.github.com/repos/" + repository + "/releases/latest");
                    Dictionary<string, object> release = JsonObject(transport.Get(endpoint, MaximumJsonBytes, true));
                    if (Boolean(release, "draft") || Boolean(release, "prerelease")) Fail("Only stable, published module releases can be installed.");
                    string tag = String(release, "tag_name");
                    string version = tag.StartsWith("v", StringComparison.Ordinal) ? tag.Substring(1) : tag;
                    int comparison = CompareVersions(version, current.Version);
                    if (comparison < 0) Fail("The release would downgrade the installed module.");
                    if (comparison == 0) return null;
                    List<object> assets = Array(release, "assets");
                    Dictionary<string, object> selected = null;
                    foreach (object item in assets)
                    {
                        Dictionary<string, object> asset = AsObject(item);
                        if (String(asset, "name") != AssetName) continue;
                        if (selected != null) Fail("The module release has duplicate package assets.");
                        selected = asset;
                    }
                    if (selected == null) Fail("The release is missing the Windows x64 module package.");
                    if (String(selected, "state") != "uploaded") Fail("The module release asset is not ready.");
                    long size = Integer(selected, "size");
                    if (size <= 0 || size > MaximumPackageBytes) Fail("The module release package exceeds the 50 MB limit.");
                    string digest = String(selected, "digest");
                    if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsHash(digest.Substring(7))) Fail("GitHub did not provide a valid SHA-256 asset digest.");
                    string expectedPath = "/" + repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + AssetName;
                    Uri download;
                    if (!Uri.TryCreate(String(selected, "browser_download_url"), UriKind.Absolute, out download) || !SafeHttps(download) || !download.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || download.AbsolutePath != expectedPath || download.Query.Length != 0 || download.Fragment.Length != 0)
                        Fail("The module asset download is outside the configured GitHub release.");
                    byte[] archive = transport.Get(download, MaximumPackageBytes, false);
                    if (archive == null || archive.Length != size || archive.Length > MaximumPackageBytes) Fail("The module asset size does not match GitHub metadata.");
                    if (!EqualHash(Hash(archive), digest.Substring(7))) Fail("The downloaded module package failed its GitHub SHA-256 digest check.");
                    string staging = NewStagingDirectory();
                    try
                    {
                        ExtractArchive(archive, staging);
                        return CommitStaging(staging, version);
                    }
                    finally { CleanupStaging(staging); }
                }
                catch (ModuleUpdateException) { throw; }
                catch (Exception ex) { throw new ModuleUpdateException("The module update could not be checked. The installed version remains available.", ex); }
            }
        }

        public void Activate(ModulePackage candidate)
        {
            lock (sync)
            {
                if (candidate == null) throw new ArgumentNullException("candidate");
                PrepareStorage();
                ParseVersion(candidate.Version);
                string expected = Path.Combine(modulesRoot, candidate.Version);
                if (!Path.GetFullPath(candidate.Directory).Equals(expected, StringComparison.OrdinalIgnoreCase)) Fail("The module candidate is outside its versioned installation directory.");
                ModulePackage validated = ValidateDirectory(expected, candidate.Version);
                if (candidate.Protocol != validated.Protocol || candidate.GameVersion != validated.GameVersion || !EqualHash(candidate.GameSha256, validated.GameSha256) || candidate.MinimumInjectorVersion != validated.MinimumInjectorVersion) Fail("The module candidate descriptor changed before activation.");
                ModulePackage current = Current();
                if (CompareVersions(validated.Version, current.Version) < 0) Fail("Activating an older module version is not allowed.");
                WritePointer(validated.Version);
            }
        }

        private void PrepareStorage()
        {
            NoReparse(dataRoot);
            System.IO.Directory.CreateDirectory(dataRoot);
            NoReparse(modulesRoot);
            System.IO.Directory.CreateDirectory(modulesRoot);
        }

        private string NewStagingDirectory()
        {
            string path = Path.Combine(modulesRoot, ".stage-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(path);
            NoReparse(path);
            return path;
        }

        private ModulePackage CommitStaging(string staging, string version)
        {
            ModulePackage staged = ValidateDirectory(staging, version);
            string destination = Path.Combine(modulesRoot, staged.Version);
            NoReparse(destination);
            if (System.IO.Directory.Exists(destination))
            {
                ModulePackage existing = ValidateDirectory(destination, staged.Version);
                foreach (string file in PackageFiles)
                    if (!EqualHash(HashFile(Path.Combine(staging, file)), HashFile(Path.Combine(destination, file)))) Fail("A different package is already stored under this module version.");
                return existing;
            }
            System.IO.Directory.Move(staging, destination);
            return ValidateDirectory(destination, version);
        }

        private void CleanupStaging(string path)
        {
            // Only remove the unique direct child allocated by this operation. Never
            // recursively remove paths learned from a release, ZIP, or current pointer.
            if (!Path.GetDirectoryName(Path.GetFullPath(path)).Equals(modulesRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith(".stage-", StringComparison.Ordinal)) return;
            try
            {
                if (!System.IO.Directory.Exists(path)) return;
                NoReparse(path);
                foreach (string file in System.IO.Directory.GetFiles(path)) { NoReparse(file); File.Delete(file); }
                System.IO.Directory.Delete(path, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ModuleUpdateException) { }
        }

        private void WritePointer(string version)
        {
            string destination = Path.Combine(dataRoot, "current.json");
            NoReparse(destination);
            string temporary = Path.Combine(dataRoot, ".current-" + Guid.NewGuid().ToString("N") + ".json");
            byte[] bytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"version\":\"" + version + "\"}\n");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void ExtractArchive(byte[] bytes, string destination)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                if (archive.Entries.Count != PackageFiles.Length) Fail("The module package must contain exactly four flat files.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!PackageFiles.Contains(entry.FullName, StringComparer.Ordinal) || !seen.Add(entry.FullName)) Fail("The module package contains an unexpected, duplicate, or unsafe archive path.");
                    int attributes = entry.ExternalAttributes;
                    if (((attributes >> 16) & 0xF000) == 0xA000 || (attributes & (int)FileAttributes.ReparsePoint) != 0) Fail("Links are not allowed in module packages.");
                    long limit = entry.FullName == "module-release.json" ? 65536 : MaximumPackageBytes;
                    if (entry.Length <= 0 || entry.Length > limit || (total += entry.Length) > MaximumPackageBytes) Fail("The extracted module package exceeds its size limit.");
                    using (Stream input = entry.Open())
                    using (var output = new FileStream(Path.Combine(destination, entry.FullName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[16384];
                        long written = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            written += count;
                            if (written > entry.Length || written > limit) Fail("An archive entry exceeded its declared size.");
                            output.Write(buffer, 0, count);
                        }
                        if (written != entry.Length) Fail("An archive entry was truncated.");
                    }
                }
            }
        }

        private static ModulePackage ValidateDirectory(string directory, string expectedVersion)
        {
            directory = Path.GetFullPath(directory);
            NoReparse(directory);
            if (!System.IO.Directory.Exists(directory)) Fail("The selected module directory is missing.");
            if (System.IO.Directory.GetDirectories(directory).Length != 0) Fail("Module directories must contain only flat package files.");
            string[] files = System.IO.Directory.GetFiles(directory);
            if (files.Length != PackageFiles.Length || files.Any(p => !PackageFiles.Contains(Path.GetFileName(p), StringComparer.Ordinal))) Fail("The module directory does not contain the exact required package files.");
            long total = 0;
            foreach (string file in files)
            {
                NoReparse(file);
                long size = new FileInfo(file).Length;
                if (size <= 0 || size > MaximumPackageBytes || (total += size) > MaximumPackageBytes) Fail("The installed module package exceeds its size limit.");
            }
            Dictionary<string, object> descriptor = JsonObject(ReadSmall(Path.Combine(directory, "module-release.json"), 65536));
            ExactKeys(descriptor, "schema", "version", "protocol", "minimumInjectorVersion", "gameVersion", "gameSha256", "files");
            if (Integer(descriptor, "schema") != 1) Fail("The module descriptor schema is unsupported.");
            string version = String(descriptor, "version");
            ParseVersion(version);
            if (expectedVersion != null && version != expectedVersion) Fail("The module version does not match the release or installed pointer.");
            string minimum = String(descriptor, "minimumInjectorVersion");
            if (CompareVersions(minimum, InjectorVersion) > 0) Fail("This module needs a newer injector.");
            if (Integer(descriptor, "protocol") != 2 || String(descriptor, "gameVersion") != SupportedGameVersion || !EqualHash(String(descriptor, "gameSha256"), SupportedGameSha256)) Fail("The module is incompatible with this injector or supported HOI4 build.");
            List<object> records = Array(descriptor, "files");
            if (records.Count != 3) Fail("The module descriptor must hash all three payload files.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in records)
            {
                Dictionary<string, object> file = AsObject(item);
                ExactKeys(file, "path", "sha256");
                string name = String(file, "path");
                if (name == "module-release.json" || !PackageFiles.Contains(name, StringComparer.Ordinal) || !seen.Add(name)) Fail("The module descriptor contains an unexpected or duplicate file path.");
                string expectedHash = String(file, "sha256");
                if (!IsHash(expectedHash) || !EqualHash(HashFile(Path.Combine(directory, name)), expectedHash)) Fail("A module file failed its SHA-256 integrity check: " + name + ".");
            }
            return new ModulePackage { Version = version, Directory = directory, Protocol = 2, MinimumInjectorVersion = minimum, GameVersion = SupportedGameVersion, GameSha256 = SupportedGameSha256 };
        }

        internal static int CompareVersions(string left, string right)
        {
            int[] a = ParseVersion(left), b = ParseVersion(right);
            for (int i = 0; i < 3; i++) { int c = a[i].CompareTo(b[i]); if (c != 0) return c; }
            return 0;
        }
        private static int[] ParseVersion(string text)
        {
            if (text == null || !Regex.IsMatch(text, @"^(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})$", RegexOptions.CultureInvariant)) Fail("Module versions must be stable semantic versions with three numeric components.");
            int[] values = new int[3];
            string[] parts = text.Split('.');
            for (int i = 0; i < 3; i++) if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) Fail("The module version is outside the supported numeric range.");
            return values;
        }
        private static byte[] ReadSmall(string path, int maximum)
        {
            NoReparse(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > maximum) Fail("A module JSON document exceeds its size limit.");
                byte[] result = new byte[(int)stream.Length];
                int offset = 0, count;
                while (offset < result.Length && (count = stream.Read(result, offset, result.Length - offset)) != 0) offset += count;
                if (offset != result.Length) Fail("A module JSON document was truncated.");
                return result;
            }
        }
        private static void NoReparse(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) Fail("Module storage cannot use links or redirected directories."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }
        private static string HashFile(string path) { using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream)); }
        internal static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static bool IsHash(string text) { return text != null && Regex.IsMatch(text, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant); }
        private static bool EqualHash(string a, string b) { return IsHash(a) && IsHash(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase); }
        internal static bool SafeHttps(Uri uri) { return uri != null && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo); }
        private static Dictionary<string, object> JsonObject(byte[] bytes)
        {
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return AsObject(StrictJson.Parse(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset)));
        }
        private static Dictionary<string, object> AsObject(object value) { var map = value as Dictionary<string, object>; if (map == null) Fail("Invalid module JSON object."); return map; }
        private static void ExactKeys(Dictionary<string, object> value, params string[] keys) { if (value.Count != keys.Length || keys.Any(key => !value.ContainsKey(key))) Fail("A module JSON object has missing or unsupported fields."); }
        private static object Field(Dictionary<string, object> map, string key) { object value; if (!map.TryGetValue(key, out value)) Fail("A required module JSON field is missing: " + key + "."); return value; }
        private static string String(Dictionary<string, object> map, string key) { var text = Field(map, key) as string; if (text == null) Fail("A module JSON string is invalid: " + key + "."); return text; }
        private static long Integer(Dictionary<string, object> map, string key) { object value = Field(map, key); if (!(value is long)) Fail("A module JSON integer is invalid: " + key + "."); return (long)value; }
        private static bool Boolean(Dictionary<string, object> map, string key) { object value = Field(map, key); if (!(value is bool)) Fail("A module JSON flag is invalid: " + key + "."); return (bool)value; }
        private static List<object> Array(Dictionary<string, object> map, string key) { var items = Field(map, key) as List<object>; if (items == null) Fail("A module JSON list is invalid: " + key + "."); return items; }
        private static void Fail(string message) { throw new ModuleUpdateException(message); }
    }

    internal sealed class GitHubModuleTransport : IModuleTransport
    {
        public byte[] Get(Uri initial, int maximumBytes, bool metadata)
        {
            if (!ModuleUpdater.SafeHttps(initial)) throw new ModuleUpdateException("Module downloads require HTTPS.");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var timer = Stopwatch.StartNew();
            Uri uri = initial;
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                if (timer.ElapsedMilliseconds >= 60000) throw new ModuleUpdateException("The module download timed out.");
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.AllowAutoRedirect = false;
                request.Timeout = (int)Math.Min(30000, 60000 - timer.ElapsedMilliseconds);
                request.ReadWriteTimeout = 20000;
                request.UserAgent = "HOI4-Injector/" + ModuleUpdater.InjectorVersion;
                request.Accept = metadata ? "application/vnd.github+json" : "application/octet-stream";
                if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        Uri next;
                        if (metadata || redirect == 5 || !Uri.TryCreate(uri, response.Headers["Location"], out next) || !ModuleUpdater.SafeHttps(next) || !AllowedAssetRedirect(next)) throw new ModuleUpdateException("The module download redirected outside GitHub's asset hosts.");
                        uri = next;
                        continue;
                    }
                    if (status != 200) throw new ModuleUpdateException("GitHub could not provide the module release.");
                    if (response.ContentLength > maximumBytes) throw new ModuleUpdateException("The module download exceeds its size limit.");
                    using (Stream input = response.GetResponseStream())
                    using (var output = new MemoryStream())
                    {
                        byte[] buffer = new byte[16384];
                        int count;
                        while (true)
                        {
                            long remaining = 60000 - timer.ElapsedMilliseconds;
                            if (remaining <= 0) { request.Abort(); throw new ModuleUpdateException("The module download timed out."); }
                            if (input.CanTimeout) input.ReadTimeout = (int)Math.Min(20000, remaining);
                            count = input.Read(buffer, 0, buffer.Length);
                            if (count == 0) break;
                            if (output.Length + count > maximumBytes) { request.Abort(); throw new ModuleUpdateException("The module download exceeds its size limit."); }
                            output.Write(buffer, 0, count);
                        }
                        return output.ToArray();
                    }
                }
            }
            throw new ModuleUpdateException("The module download exceeded the redirect limit.");
        }
        private static bool AllowedAssetRedirect(Uri uri)
        {
            return uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Small strict JSON reader: refuses duplicate keys, invalid UTF-8 (at the caller),
    // comments, trailing input, control characters and excessive nesting.
    internal sealed class StrictJson
    {
        private readonly string text;
        private int index;
        private StrictJson(string text) { this.text = text; }
        public static object Parse(string text)
        {
            var reader = new StrictJson(text);
            object value = reader.Value(0);
            reader.Space();
            if (reader.index != text.Length) reader.Invalid();
            return value;
        }
        private object Value(int depth)
        {
            if (depth > 32) Invalid();
            Space();
            if (index >= text.Length) Invalid();
            char c = text[index];
            if (c == '"') return ReadString();
            if (c == '{')
            {
                index++;
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                Space();
                if (Take('}')) return result;
                while (true)
                {
                    Space();
                    if (index >= text.Length || text[index] != '"') Invalid();
                    string key = ReadString();
                    Space();
                    if (!Take(':') || result.ContainsKey(key)) Invalid();
                    result.Add(key, Value(depth + 1));
                    Space();
                    if (Take('}')) return result;
                    if (!Take(',')) Invalid();
                }
            }
            if (c == '[')
            {
                index++;
                var result = new List<object>();
                Space();
                if (Take(']')) return result;
                while (true)
                {
                    result.Add(Value(depth + 1));
                    Space();
                    if (Take(']')) return result;
                    if (!Take(',')) Invalid();
                }
            }
            if (Literal("true")) return true;
            if (Literal("false")) return false;
            if (Literal("null")) return null;
            int start = index;
            Take('-');
            if (!Take('0')) { if (index >= text.Length || text[index] < '1' || text[index] > '9') Invalid(); while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++; }
            bool fractional = false;
            if (Take('.')) { fractional = true; Digits(); }
            if (Take('e') || Take('E')) { fractional = true; if (!Take('+')) Take('-'); Digits(); }
            string number = text.Substring(start, index - start);
            long integer;
            if (!fractional && long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer)) return integer;
            double floating;
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out floating) && !double.IsInfinity(floating) && !double.IsNaN(floating)) return floating;
            Invalid();
            return null;
        }
        private void Digits() { int start = index; while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++; if (start == index) Invalid(); }
        private string ReadString()
        {
            index++;
            var value = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return value.ToString();
                if (c < 0x20) Invalid();
                if (c != '\\') { value.Append(c); continue; }
                if (index >= text.Length) Invalid();
                c = text[index++];
                if (c == '"' || c == '\\' || c == '/') value.Append(c);
                else if (c == 'b') value.Append('\b');
                else if (c == 'f') value.Append('\f');
                else if (c == 'n') value.Append('\n');
                else if (c == 'r') value.Append('\r');
                else if (c == 't') value.Append('\t');
                else if (c == 'u')
                {
                    if (index + 4 > text.Length) Invalid();
                    int code;
                    if (!int.TryParse(text.Substring(index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) Invalid();
                    value.Append((char)code); index += 4;
                }
                else Invalid();
            }
            Invalid();
            return null;
        }
        private void Space() { while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\n' || text[index] == '\r')) index++; }
        private bool Take(char c) { if (index < text.Length && text[index] == c) { index++; return true; } return false; }
        private bool Literal(string literal) { if (index + literal.Length <= text.Length && string.CompareOrdinal(text, index, literal, 0, literal.Length) == 0) { index += literal.Length; return true; } return false; }
        private void Invalid() { throw new ModuleUpdateException("A module JSON document is malformed or contains duplicate fields."); }
    }
}
