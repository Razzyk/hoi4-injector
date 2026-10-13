using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using HOI4Installer;

internal static class InstallerTests
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; }
    private static void Reject(Action action, string name)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, name);
    }
    private static MemoryStream Package(string replacement, bool duplicate, bool symlink)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (string name in InstallCore.PayloadFiles)
            {
                string entryName = name == "HOI4Injector.exe" && replacement != null ? replacement : name;
                ZipArchiveEntry entry = zip.CreateEntry(entryName);
                if (symlink && name == "HOI4Injector.exe") entry.ExternalAttributes = 0xa000 << 16;
                using (var output = new StreamWriter(entry.Open())) output.Write("fixture " + name);
            }
            if (duplicate) using (var output = new StreamWriter(zip.CreateEntry("HOI4Injector.exe").Open())) output.Write("duplicate");
        }
        stream.Position = 0; return stream;
    }
    public static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Expected fixture folder.");
        string basePath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(basePath);
        string installed = Path.Combine(basePath, "installed");
        using (MemoryStream payload = Package(null, false, false)) InstallCore.Extract(payload, installed);
        Check(InstallCore.PayloadFiles.All(name => File.Exists(InstallCore.Destination(installed, name))), "all payload files extracted");
        Check(InstallCore.ReadRecord(installed).Version == "0.4.0", "installation record bound to folder");
        using (MemoryStream payload = Package(null, false, false)) InstallCore.Extract(payload, installed);
        Check(File.ReadAllText(Path.Combine(installed, "HOI4Injector.exe")).StartsWith("fixture "), "owned reinstall allowed");
        Reject(delegate { InstallCore.NormalizeDirectory("relative-folder"); }, "relative target refused");
        Reject(delegate { InstallCore.NormalizeDirectory(Path.GetPathRoot(basePath)); }, "drive root refused");
        Reject(delegate { InstallCore.NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); }, "local app data root refused");
        Reject(delegate { InstallCore.NormalizeDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HOI4Injector")); }, "userdata folder refused");
        string[] attacks = { "../escaped.exe", "/escaped.exe", "C:/escaped.exe", "bundled-module/../escaped.exe", "bundled-module\\HOI4Admission.dll", "runtime/session.json", "unexpected.txt", "HOI4INJECTOR.exe" };
        foreach (string attack in attacks)
        {
            string target = Path.Combine(basePath, "attack-" + checks);
            using (MemoryStream payload = Package(attack, false, false)) Reject(delegate { InstallCore.Extract(payload, target); }, "refuse " + attack);
            Check(!Directory.Exists(target), "invalid package did not create target");
        }
        using (MemoryStream payload = Package(null, true, false)) Reject(delegate { InstallCore.Extract(payload, Path.Combine(basePath, "duplicate")); }, "duplicate archive file refused");
        using (MemoryStream payload = Package(null, false, true)) Reject(delegate { InstallCore.Extract(payload, Path.Combine(basePath, "symlink")); }, "archive symlink refused");
        using (MemoryStream payload = Package(null, false, false))
        {
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Update, true)) zip.GetEntry("AdmissionLoader.exe").Delete();
            payload.Position = 0;
            Reject(delegate { InstallCore.Extract(payload, Path.Combine(basePath, "incomplete")); }, "incomplete package refused");
        }
        string occupied = Path.Combine(basePath, "occupied"); Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        using (MemoryStream payload = Package(null, false, false)) Reject(delegate { InstallCore.Extract(payload, occupied); }, "unowned nonempty folder refused");
        Check(File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "keep", "unowned file preserved");
        string original = File.ReadAllText(Path.Combine(installed, "HOI4Injector.exe"));
        using (var locked = new FileStream(Path.Combine(installed, "AdmissionLoader.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (MemoryStream payload = Package(null, false, false))
        {
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Update, true))
            {
                zip.GetEntry("HOI4Injector.exe").Delete();
                using (var writer = new StreamWriter(zip.CreateEntry("HOI4Injector.exe").Open())) writer.Write("new fixture executable");
            }
            payload.Position = 0;
            bool failed = false;
            try { InstallCore.Extract(payload, installed); } catch (IOException) { failed = true; }
            Check(failed, "locked running binary prevents replacement");
        }
        Check(File.ReadAllText(Path.Combine(installed, "HOI4Injector.exe")) == original, "failed replacement preserves existing app");
        Check(InstallCore.ReadRecord(installed).Product == "HOI4Injector", "failed replacement preserves ownership record");
        string marker = Path.Combine(installed, InstallCore.Marker);
        string record = File.ReadAllText(marker); File.WriteAllText(marker, record.Replace("HOI4Injector", "OtherProduct"));
        Reject(delegate { InstallCore.RemoveFiles(installed); }, "uninstall rejects wrong product");
        File.WriteAllText(marker, record);
        File.WriteAllText(Path.Combine(installed, "my-note.txt"), "keep me");
        InstallCore.RemoveFiles(installed);
        Check(File.ReadAllText(Path.Combine(installed, "my-note.txt")) == "keep me", "uninstall preserves unknown files");
        Check(InstallCore.PayloadFiles.All(name => !File.Exists(Path.Combine(installed, name.Replace('/', Path.DirectorySeparatorChar)))), "uninstall removes only declared payload");
        Check(!File.Exists(marker), "uninstall removes record");
        Console.WriteLine("PASS: " + checks + " installer safety and extraction checks");
        return 0;
    }
}
