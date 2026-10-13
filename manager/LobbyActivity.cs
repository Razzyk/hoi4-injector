using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HOI4Admission
{
    internal sealed class LobbyActivitySnapshot
    {
        public string Rejected = "";
        public string Allowed = "";
    }

    internal static class LobbyActivity
    {
        private const int TailBytes = 16384;

        public static LobbyActivitySnapshot Read(Configuration config, ManagedGame[] games)
        {
            var result = new LobbyActivitySnapshot();
            if (config == null || games == null || string.IsNullOrEmpty(config.RuntimeDirectory)) return result;
            DateTime rejectedUtc = DateTime.MinValue, allowedUtc = DateTime.MinValue;
            // Native timestamps have second precision. Later lines in the same
            // file win ties; ties across files use the caller's game scan order.
            foreach (ManagedGame game in games)
            {
                if (game == null || game.Pid <= 0 || game.Creation <= 0) continue;
                try
                {
                    DateTime createdUtc = DateTime.FromFileTimeUtc(game.Creation);
                    string path = Path.Combine(config.RuntimeDirectory, "dll-" + game.Pid + ".log");
                    byte[] tail;
                    int used = 0;
                    long start;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        long length = stream.Length;
                        int count = (int)Math.Min(length, TailBytes);
                        start = length - count;
                        tail = new byte[count];
                        stream.Seek(start, SeekOrigin.Begin);
                        while (used < count)
                        {
                            int read = stream.Read(tail, used, count - used);
                            if (read == 0) break;
                            used += read;
                        }
                    }
                    string[] lines = Encoding.UTF8.GetString(tail, 0, used).Split('\n');
                    // Discard a clipped first line and any unfinished last line.
                    for (int i = start > 0 ? 1 : 0; i < lines.Length - 1; ++i)
                    {
                        string line = lines[i].TrimEnd('\r');
                        if (line.Length < 22) continue;
                        DateTime timestamp;
                        if (!DateTime.TryParseExact(line.Substring(0, 20), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out timestamp) || timestamp < createdUtc) continue;
                        string[] fields = line.Substring(21).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        int pid;
                        if (fields.Length < 3 || !fields[0].StartsWith("pid=", StringComparison.Ordinal) ||
                            !int.TryParse(fields[0].Substring(4), out pid) || pid != game.Pid) continue;
                        bool rejected = fields[1] == "LOBBY_JOIN_REJECTED";
                        bool allowed = fields[1] == "LOBBY_JOIN_ALLOWED";
                        if (!rejected && !allowed) continue;
                        string reason = null;
                        for (int field = 2; field < fields.Length; ++field)
                            if (fields[field].StartsWith("reason=", StringComparison.Ordinal)) reason = fields[field].Substring(7);
                        if (!SafeReason(reason)) continue;
                        string player = string.IsNullOrEmpty(game.Player) ? "unassigned player" : game.Player;
                        string text = "Host " + player + " (PID " + game.Pid + "): " + Explain(reason) + " [" + reason + "]";
                        if (rejected && timestamp >= rejectedUtc) { rejectedUtc = timestamp; result.Rejected = text; }
                        if (allowed && timestamp >= allowedUtc) { allowedUtc = timestamp; result.Allowed = text; }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return result;
        }

        private static bool SafeReason(string reason)
        {
            if (string.IsNullOrEmpty(reason) || reason.Length > 64) return false;
            foreach (char character in reason)
                if ((character < 'A' || character > 'Z') && (character < '0' || character > '9') && character != '_') return false;
            return true;
        }

        private static string Explain(string reason)
        {
            if (reason == "DLL_TICKET_MISSING" || reason == "DLL_TICKET_INVALID") return "Required DLL verification ticket missing or invalid";
            if (reason == "HOST_NOT_VERIFIED") return "Host not verified";
            if (reason == "VERIFICATION_SERVICE_UNAVAILABLE") return "Verification service unavailable";
            if (reason == "DLL_TICKET_VERIFIED") return "Verified DLL ticket accepted";
            string text = reason.ToLowerInvariant().Replace('_', ' ');
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }
}
