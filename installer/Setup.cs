using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HOI4Installer
{
    internal static class SetupProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--extract-only")
                {
                    using (Stream payload = InstallCore.OpenPayload()) InstallCore.Extract(payload, args[1]);
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("Usage: HOI4Injector-Setup.exe [--extract-only absolute-folder]");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new SetupForm()) Application.Run(form);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                if (args.Length == 0) MessageBox.Show(error.Message, "HOI4 Injector Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
    internal sealed class SetupForm : Form
    {
        private readonly TextBox destination = new TextBox();
        private readonly CheckBox desktop = new CheckBox(), startup = new CheckBox(), launch = new CheckBox();
        private readonly Button install = new Button();
        private readonly Label status = new Label();
        public SetupForm()
        {
            Text = "HOI4 Injector Setup"; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(570, 365); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            Font = new Font("Segoe UI", 10f); AutoScaleMode = AutoScaleMode.Dpi;
            var title = new Label { Text = "Install HOI4 Injector", Font = new Font("Segoe UI", 17f, FontStyle.Bold), AutoSize = true, Location = new Point(25, 22) };
            var description = new Label { Text = "Start the injector, then launch HOI4 as usual.\r\nIt will load and update your admission mod automatically.", Location = new Point(27, 64), Size = new Size(520, 55) };
            var pathLabel = new Label { Text = "Install folder", AutoSize = true, Location = new Point(27, 125) };
            destination.SetBounds(27, 150, 430, 27); destination.Text = InstallCore.DefaultDirectory;
            var browse = new Button { Text = "Browse…", Location = new Point(465, 149), Size = new Size(80, 30) };
            browse.Click += delegate { using (var picker = new FolderBrowserDialog { Description = "Choose an HOI4 Injector application folder", SelectedPath = destination.Text }) if (picker.ShowDialog(this) == DialogResult.OK) destination.Text = picker.SelectedPath; };
            desktop.Text = "Create a desktop shortcut"; desktop.Checked = true; desktop.SetBounds(27, 193, 480, 25);
            startup.Text = "Start when I sign in to Windows"; startup.SetBounds(27, 224, 480, 25);
            launch.Text = "Launch HOI4 Injector after installation"; launch.Checked = true; launch.SetBounds(27, 255, 480, 25);
            status.SetBounds(27, 300, 350, 48); status.Text = "Installs for your Windows account.";
            install.Text = "Install"; install.SetBounds(440, 310, 105, 34); install.Click += Install;
            Controls.AddRange(new Control[] { title, description, pathLabel, destination, browse, desktop, startup, launch, status, install });
            AcceptButton = install;
        }
        private void Install(object sender, EventArgs args)
        {
            install.Enabled = false; status.Text = "Installing…"; UseWaitCursor = true;
            try
            {
                string directory = InstallCore.NormalizeDirectory(destination.Text);
                using (Stream payload = InstallCore.OpenPayload()) InstallCore.Extract(payload, directory);
                InstallCore.Register(directory, desktop.Checked, startup.Checked);
                if (launch.Checked) Process.Start(new ProcessStartInfo { FileName = Path.Combine(directory, "HOI4Injector.exe"), WorkingDirectory = directory, UseShellExecute = true });
                Close();
            }
            catch (Exception error) { status.Text = "Installation did not complete."; MessageBox.Show(this, error.Message + "\r\nClose HOI4 Injector before reinstalling.", "HOI4 Injector Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); install.Enabled = true; }
            finally { UseWaitCursor = false; }
        }
    }
}
