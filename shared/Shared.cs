using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace HOI4Admission
{
    public sealed class Player { public string Id; public string Secret; public bool Allowed = true; }
    public sealed class Exclusion { public int Pid; public long Creation; }
    public sealed class Configuration
    {
        public int Protocol = 2;
        public string SessionId, PipeName, GamePath, DllPath, DllHash, ManifestPath, ManifestHash, RuntimeDirectory;
        public string GameVersion = "1.19.3.0";
        public string GameHash = "7dc947be34970da1e1c787bcddbe7f62b610ff258aebf8f06aa8b348f3a031d5";
        public int StartupDelayMs = 5000;
        public Player[] Players;
        public Exclusion[] Exclusions = new Exclusion[0];
    }
    public sealed class ClientRecord
    {
        public int Pid;
        public long Creation;
        public string Player, Session, Lease, Detail;
        public bool Eligible;
        public DateTime LastSeenUtc;
        public long LastSeenTicks;
    }
    public sealed class ServiceSnapshot { public string SessionId; public bool Running = true; public ClientRecord[] Clients; }
    public sealed class AdmissionDecision { public bool Eligible; public string Reason; public int Pid; public long Creation; }

    public static class Files
    {
        public static JavaScriptSerializer Json { get { return new JavaScriptSerializer(); } }
        public static string Hash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }
        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static byte[] Unhex(string text)
        {
            if (text == null || text.Length != 64) throw new FormatException("Expected 32-byte hexadecimal credential");
            return Enumerable.Range(0, 32).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
        }
        public static string RandomHex()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Hex(bytes);
        }
        public static Configuration Load(string path)
        {
            Configuration c = Json.Deserialize<Configuration>(File.ReadAllText(path));
            if (c == null || c.Protocol != 2 || c.StartupDelayMs < 0 || c.StartupDelayMs > 120000 || c.Players == null || c.Players.Length == 0) throw new FormatException("Invalid session configuration");
            if (!SafeId(c.SessionId) || !SafeId(c.PipeName) || c.Players.Any(p => !SafeId(p.Id))) throw new FormatException("Invalid session or player identifier");
            foreach (Player p in c.Players) Unhex(p.Secret);
            if (c.Players.Select(p => p.Id).Distinct().Count() != c.Players.Length) throw new FormatException("Duplicate player identity");
            foreach (string file in new[] { c.GamePath, c.DllPath, c.ManifestPath }) if (!Path.IsPathRooted(file) || !File.Exists(file)) throw new FileNotFoundException("Missing session dependency", file);
            if (!string.Equals(Path.GetFileName(c.GamePath), "hoi4.exe", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Game executable must be hoi4.exe");
            if (c.Exclusions == null) c.Exclusions = new Exclusion[0];
            return c;
        }
        private static bool SafeId(string id) { return !string.IsNullOrEmpty(id) && id.Length < 64 && id.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_'); }
        public static void MakePrivate(string directory)
        {
            Directory.CreateDirectory(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(directory, security);
        }
        public static void Save(Configuration c, string path) { File.WriteAllText(path, Json.Serialize(c)); }
        public static long Creation(Process process) { return process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
        public static string Identity(int pid, long creation) { return pid + ":" + creation; }
    }

    public static class Wire
    {
        public static void Send(PipeStream pipe, string message)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(message);
            if (bytes.Length == 0 || bytes.Length > 8192) throw new IOException("Invalid message size");
            IAsyncResult op = pipe.BeginWrite(bytes, 0, bytes.Length, null, null);
            try { if (!op.AsyncWaitHandle.WaitOne(7000)) throw new TimeoutException("Pipe write timed out"); pipe.EndWrite(op); }
            finally { op.AsyncWaitHandle.Close(); }
        }
        public static string Receive(PipeStream pipe)
        {
            var bytes = new byte[8192];
            int used = 0;
            do
            {
                IAsyncResult op = pipe.BeginRead(bytes, used, bytes.Length - used, null, null);
                int count;
                try { if (!op.AsyncWaitHandle.WaitOne(7000)) throw new TimeoutException("Pipe read timed out"); count = pipe.EndRead(op); }
                finally { op.AsyncWaitHandle.Close(); }
                if (count == 0) throw new EndOfStreamException();
                used += count;
                if (used == bytes.Length && !pipe.IsMessageComplete) throw new IOException("Message too large");
            } while (!pipe.IsMessageComplete);
            return Encoding.ASCII.GetString(bytes, 0, used);
        }
        public static string Control(Configuration c, string command)
        {
            using (var pipe = new NamedPipeClientStream(".", c.PipeName + "-control", PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(2000);
                pipe.ReadMode = PipeTransmissionMode.Message;
                Send(pipe, command);
                return Receive(pipe);
            }
        }
    }

    public sealed class AdmissionPolicy
    {
        private readonly Configuration config;
        public AdmissionPolicy(Configuration c) { config = c; }
        public string Authenticate(string[] fields, string nonce)
        {
            if (fields.Length != 12 || fields[0] != "VERIFY") return "INVALID_FRAME";
            if (fields[1] != config.Protocol.ToString()) return "PROTOCOL_MISMATCH";
            if (fields[4] != config.SessionId) return "SESSION_MISMATCH";
            Player player = config.Players.FirstOrDefault(p => p.Id == fields[5]);
            if (player == null || !player.Allowed) return "PLAYER_NOT_PERMITTED";
            if (fields[6] != config.GameVersion || fields[7] != config.GameHash) return "GAME_VERSION_MISMATCH";
            if (fields[8] != config.ManifestHash) return "MOD_MANIFEST_MISMATCH";
            if (fields[9] != config.DllHash) return "DLL_BUILD_MISMATCH";
            if (fields[10] != nonce) return "CHALLENGE_MISMATCH";
            using (var hmac = new HMACSHA256(Files.Unhex(player.Secret)))
            {
                string expected = Files.Hex(hmac.ComputeHash(Encoding.ASCII.GetBytes(string.Join("|", fields.Take(11)))));
                if (fields[11].Length != expected.Length) return "AUTHENTICATION_FAILED";
                int difference = 0;
                for (int i = 0; i < expected.Length; ++i) difference |= expected[i] ^ fields[11][i];
                if (difference != 0) return "AUTHENTICATION_FAILED";
            }
            return null;
        }
    }
}
