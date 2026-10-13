using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace HOI4Installer
{
    internal static class UninstallProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--remove-install")
                {
                    int pid = int.Parse(args[2]);
                    try { using (Process parent = Process.GetProcessById(pid)) if (!parent.WaitForExit(10000)) throw new IOException("The uninstaller is still running."); }
                    catch (ArgumentException) { }
                    string directory = InstallCore.NormalizeDirectory(args[1]);
                    InstallCore.ReadRecord(directory);
                    InstallCore.Unregister(directory); InstallCore.RemoveFiles(directory);
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("Invalid uninstall arguments.");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                string installDirectory = InstallCore.NormalizeDirectory(AppDomain.CurrentDomain.BaseDirectory);
                InstallCore.ReadRecord(installDirectory);
                if (MessageBox.Show("Remove HOI4 Injector?\r\nYour settings and downloaded mods will be kept.", "Uninstall HOI4 Injector", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
                string copy = Path.Combine(Path.GetTempPath(), "HOI4InjectorUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(typeof(UninstallProgram).Assembly.Location, copy);
                Process.Start(new ProcessStartInfo { FileName = copy, Arguments = "--remove-install \"" + installDirectory + "\" " + Process.GetCurrentProcess().Id, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                MessageBox.Show(error.Message + "\r\nClose HOI4 Injector and try again.", "Uninstall HOI4 Injector", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
