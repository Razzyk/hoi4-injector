using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HOI4Admission
{
    internal static class GameDiscovery
    {
        public static string Find(bool interactive)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string path = key == null ? null : key.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(path)) roots.Add(path);
            }
            foreach (string steam in new List<string>(roots))
            {
                string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (!File.Exists(libraries)) continue;
                    foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s*\"([^\"]+)\""))
                        roots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string root in roots)
            {
                string game = Path.Combine(root, "steamapps", "common", "Hearts of Iron IV", "hoi4.exe");
                if (File.Exists(game)) return Path.GetFullPath(game);
            }
            if (interactive)
            {
                using (var picker = new OpenFileDialog { Title = "Select Hearts of Iron IV", Filter = "Hearts of Iron IV|hoi4.exe", CheckFileExists = true })
                    if (picker.ShowDialog() == DialogResult.OK) return picker.FileName;
            }
            throw new FileNotFoundException("Select your HOI4 executable, or supply --game-exe when using the command line.");
        }
    }
}
