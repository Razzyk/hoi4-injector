using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace HOI4Installer
{
    public sealed class InstallRecord
    {
        public string Product = "HOI4Injector";
        public string Version = "0.4.0";
        public string Directory;
        public string[] Files;
    }

    public static class InstallCore
    {
        public const string Marker = ".hoi4injector-install.json";
        public const string StartupName = "HOI4Injector";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HOI4Injector";
        public static readonly string[] PayloadFiles = {
            "HOI4Injector.exe", "HOI4Injector.exe.config", "AdmissionLoader.exe", "Uninstall.exe",
            "release-source.json", "README-Run.txt", "bundled-module/HOI4Admission.dll",
            "bundled-module/AdmissionService.exe", "bundled-module/mod-manifest.json", "bundled-module/module-release.json"
        };
        public static string DefaultDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HOI4Injector"); }
        }
        public static string NormalizeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new InvalidDataException("Choose an absolute installation folder.");
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string drive = Path.GetPathRoot(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, drive, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a folder, not a drive root.");
            string[] forbidden = {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HOI4Injector")
            };
            foreach (string item in forbidden)
            {
                string full = Path.GetFullPath(item).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(item, forbidden[0], StringComparison.OrdinalIgnoreCase) && root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(item, forbidden[3], StringComparison.OrdinalIgnoreCase) && root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Choose a separate application folder.");
            }
            CheckParents(root);
            return root;
        }
        private static void CheckParents(string path)
        {
            DirectoryInfo current = new DirectoryInfo(path);
            while (current != null)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The installation folder cannot use a junction or symbolic link.");
                current = current.Parent;
            }
        }
        public static string Destination(string directory, string name)
        {
            if (!PayloadFiles.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Unexpected file in the installation package: " + name);
            if (name.Contains("\\") || name.Contains(":") || name.StartsWith("/") || name.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new InvalidDataException("Unsafe installation package path.");
            string full = Path.GetFullPath(Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installation package path escapes its folder.");
            CheckParents(Path.GetDirectoryName(full));
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("An installed file is a symbolic link.");
            return full;
        }
        public static InstallRecord ReadRecord(string directory)
        {
            string path = Path.Combine(directory, Marker);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("This folder is not an HOI4 Injector installation.");
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Invalid installation record.");
            InstallRecord record = new JavaScriptSerializer().Deserialize<InstallRecord>(File.ReadAllText(path));
            if (record == null || record.Product != "HOI4Injector" || record.Files == null ||
                !string.Equals(NormalizeDirectory(record.Directory), NormalizeDirectory(directory), StringComparison.OrdinalIgnoreCase) ||
                record.Files.Length != PayloadFiles.Length || !new HashSet<string>(record.Files, StringComparer.Ordinal).SetEquals(PayloadFiles))
                throw new InvalidDataException("The installation record does not match this folder.");
            return record;
        }
        public static void Extract(Stream payload, string target)
        {
            string directory = NormalizeDirectory(target);
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) ReadRecord(directory);
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                long total = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    Destination(directory, entry.FullName);
                    if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("The installation package contains a symbolic link.");
                    if (entry.Length < 1 || entry.Length > 100 * 1024 * 1024) throw new InvalidDataException("Invalid installation package file size.");
                    total += entry.Length;
                    if (total > 250 * 1024 * 1024 || entries.ContainsKey(entry.FullName)) throw new InvalidDataException("Invalid installation package contents.");
                    entries.Add(entry.FullName, entry);
                }
                if (entries.Count != PayloadFiles.Length || PayloadFiles.Any(name => !entries.ContainsKey(name))) throw new InvalidDataException("The installation package is incomplete.");
                // Validate and stage the complete package before changing an existing installation.
                string stage = Path.Combine(Path.GetTempPath(), "HOI4InjectorSetup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    foreach (string name in PayloadFiles)
                    {
                        string staged = Destination(stage, name);
                        Directory.CreateDirectory(Path.GetDirectoryName(staged));
                        using (Stream source = entries[name].Open())
                        using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                        if (new FileInfo(staged).Length != entries[name].Length) throw new InvalidDataException("Incomplete installation package file.");
                    }
                    CheckParents(directory);
                    Directory.CreateDirectory(directory);
                    string originalRecord = File.Exists(Path.Combine(directory, Marker)) ? File.ReadAllText(Path.Combine(directory, Marker)) : null;
                    var replaced = new List<string>();
                    var originals = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        foreach (string name in PayloadFiles)
                        {
                            string destination = Destination(directory, name);
                            if (File.Exists(destination))
                            {
                                string backup = Path.Combine(stage, "backup", name.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                                File.Copy(destination, backup);
                                originals.Add(name);
                            }
                        }
                        foreach (string name in PayloadFiles)
                        {
                            string destination = Destination(directory, name);
                            Directory.CreateDirectory(Path.GetDirectoryName(destination));
                            replaced.Add(name);
                            File.Copy(Destination(stage, name), destination, true);
                        }
                        File.WriteAllText(Path.Combine(directory, Marker), new JavaScriptSerializer().Serialize(new InstallRecord { Directory = directory, Files = PayloadFiles }));
                    }
                    catch
                    {
                        // Restore an existing installation if a locked file or copy failure interrupted replacement.
                        foreach (string name in replaced)
                        {
                            string destination = Destination(directory, name);
                            if (originals.Contains(name)) File.Copy(Path.Combine(stage, "backup", name.Replace('/', Path.DirectorySeparatorChar)), destination, true);
                            else if (File.Exists(destination)) File.Delete(destination);
                        }
                        if (originalRecord != null) File.WriteAllText(Path.Combine(directory, Marker), originalRecord);
                        else if (File.Exists(Path.Combine(directory, Marker))) File.Delete(Path.Combine(directory, Marker));
                        throw;
                    }
                }
                finally
                {
                    foreach (string name in PayloadFiles) { string file = Destination(stage, name); if (File.Exists(file)) File.Delete(file); }
                    string backup = Path.Combine(stage, "backup");
                    foreach (string name in PayloadFiles)
                    {
                        string file = Path.Combine(backup, name.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(file)) File.Delete(file);
                    }
                    string backupBundle = Path.Combine(backup, "bundled-module");
                    if (Directory.Exists(backupBundle) && !Directory.EnumerateFileSystemEntries(backupBundle).Any()) Directory.Delete(backupBundle);
                    if (Directory.Exists(backup) && !Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup);
                    string bundle = Path.Combine(stage, "bundled-module");
                    if (Directory.Exists(bundle) && !Directory.EnumerateFileSystemEntries(bundle).Any()) Directory.Delete(bundle);
                    if (!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
                }
            }
        }
        public static Stream OpenPayload()
        {
            Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("HOI4Installer.Payload.zip");
            if (payload == null) throw new InvalidDataException("The setup package is missing its files.");
            return payload;
        }
        private static string ShortcutPath(bool desktop)
        {
            return Path.Combine(Environment.GetFolderPath(desktop ? Environment.SpecialFolder.DesktopDirectory : Environment.SpecialFolder.Programs), "HOI4 Injector.lnk");
        }
        public static void Shortcut(string directory, bool desktop)
        {
            string path = ShortcutPath(desktop);
            object shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Path.Combine(directory, "HOI4Injector.exe") });
                shortcut.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { directory });
                shortcut.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Automatically manage your HOI4 admission mod" });
                shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
        public static void Register(string directory, bool desktop, bool startup)
        {
            Shortcut(directory, false);
            if (desktop) Shortcut(directory, true);
            if (startup)
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) key.SetValue(StartupName, "\"" + Path.Combine(directory, "HOI4Injector.exe") + "\" --minimized");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "HOI4 Injector"); key.SetValue("DisplayVersion", "0.4.0");
                key.SetValue("Publisher", "Razzyk"); key.SetValue("InstallLocation", directory);
                key.SetValue("UninstallString", "\"" + Path.Combine(directory, "Uninstall.exe") + "\"");
                key.SetValue("DisplayIcon", Path.Combine(directory, "HOI4Injector.exe"));
                key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        private static void RemoveShortcut(string directory, bool desktop)
        {
            string path = ShortcutPath(desktop);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            object shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                string target = (string)shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                if (string.Equals(target, Path.Combine(directory, "HOI4Injector.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
        public static void Unregister(string directory)
        {
            RemoveShortcut(directory, false); RemoveShortcut(directory, true);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                string expected = "\"" + Path.Combine(directory, "HOI4Injector.exe") + "\" --minimized";
                if (key != null && string.Equals(key.GetValue(StartupName) as string, expected, StringComparison.Ordinal)) key.DeleteValue(StartupName, false);
            }
            bool owned;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey)) owned = key != null && string.Equals(key.GetValue("InstallLocation") as string, directory, StringComparison.OrdinalIgnoreCase);
            if (owned) Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
        }
        public static void RemoveFiles(string target)
        {
            string directory = NormalizeDirectory(target);
            InstallRecord record = ReadRecord(directory);
            foreach (string name in record.Files)
            {
                string file = Destination(directory, name);
                if (File.Exists(file)) File.Delete(file);
            }
            File.Delete(Path.Combine(directory, Marker));
            string bundle = Path.Combine(directory, "bundled-module");
            if (Directory.Exists(bundle) && !Directory.EnumerateFileSystemEntries(bundle).Any()) Directory.Delete(bundle);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
