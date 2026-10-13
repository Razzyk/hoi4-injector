using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HOI4Admission
{
    public sealed class GuardForm : Form
    {
        private readonly Configuration config;
        private readonly string configPath;
        private readonly ManagerEngine engine;
        private readonly Label title = new Label();
        private readonly DataGridView grid = new DataGridView();
        private readonly ToolStripStatusLabel footer = new ToolStripStatusLabel();
        private readonly Label refusedJoin = new Label();
        private readonly Label allowedJoin = new Label();
        private readonly Label updateStatus = new Label();
        private readonly UpdateCoordinator updates;
        private readonly ReleaseSource releaseSource;
        private readonly string dataRoot;
        private readonly Timer timer = new Timer();
        private readonly NotifyIcon tray;
        private readonly HOI4Manager.NativeMenu ui = new HOI4Manager.NativeMenu();
        private bool busy, exiting, hideMinimized = true;
        private ManagedGame[] rows = new ManagedGame[0];
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupName = "HOI4Injector";
        internal GuardForm(Configuration c, string path, string binaries, bool minimized, UpdateCoordinator updateCoordinator, ReleaseSource source, string root)
        {
            config = c; configPath = Path.GetFullPath(path); engine = new ManagerEngine(c, binaries);
            updates = updateCoordinator; releaseSource = source; dataRoot = root;
            Text = "HOI4 Injector";
            Icon = SystemIcons.Application;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(760, 430);
            MinimumSize = new Size(680, 430);
            StartPosition = FormStartPosition.CenterScreen;
            MenuStrip menu = ui.BuildMenu();
            MainMenuStrip = menu;
            Controls.Add(menu);
            var status = new StatusStrip();
            footer.Text = "Local verification service | DLL protocol 2";
            status.Items.Add(footer); Controls.Add(status);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15, 10, 15, 8), ColumnCount = 1, RowCount = 6 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            title.Text = "Waiting for HOI4"; title.Font = new Font("Segoe UI", 12, FontStyle.Bold); title.Dock = DockStyle.Fill;
            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.Columns.Add("Pid", "PID"); grid.Columns[0].FillWeight = 50;
            grid.Columns.Add("Player", "Player"); grid.Columns[1].FillWeight = 100;
            grid.Columns.Add("State", "Status"); grid.Columns[2].FillWeight = 180;
            grid.BackgroundColor = SystemColors.Window;
            var note = new Label { Text = "Verified hosts require a one-use DLL ticket before accepting a native join.\r\nThis test service verifies clients running on this machine.", Dock = DockStyle.Fill };
            refusedJoin.Text = "Last refused join: none recorded"; refusedJoin.Dock = DockStyle.Fill; refusedJoin.ForeColor = Color.DarkRed;
            allowedJoin.Text = "Last authorized join: none recorded"; allowedJoin.Dock = DockStyle.Fill; allowedJoin.ForeColor = Color.DarkGreen;
            panel.Controls.Add(title, 0, 0); panel.Controls.Add(grid, 0, 1); panel.Controls.Add(note, 0, 2);
            updateStatus.Dock = DockStyle.Fill; updateStatus.Text = "Automatic DLL updates enabled";
            panel.Controls.Add(refusedJoin, 0, 3); panel.Controls.Add(allowedJoin, 0, 4); panel.Controls.Add(updateStatus, 0, 5); Controls.Add(panel); panel.BringToFront();
            BindActions(menu);
            var context = new ContextMenuStrip();
            context.Items.Add("Open", null, (s, e) => Restore()); context.Items.Add("Exit manager", null, (s, e) => Exit());
            tray = new NotifyIcon { Icon = Icon, Text = "HOI4 Injector", Visible = true, ContextMenuStrip = context };
            tray.DoubleClick += (s, e) => Restore();
            timer.Interval = 1000; timer.Tick += async (s, e) => await Tick();
            Shown += async (s, e) => { timer.Start(); await Tick(); if (minimized) WindowState = FormWindowState.Minimized; };
            Resize += (s, e) => { if (WindowState == FormWindowState.Minimized && hideMinimized) Hide(); };
            FormClosing += (s, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        }
        private string StartupCommand { get { return "\"" + Application.ExecutablePath + "\" --config \"" + configPath + "\" --data-dir \"" + dataRoot + "\" --minimized"; } }
        private void BindActions(MenuStrip menu)
        {
            ui.Actions["actionOpen_manager_folder"].Click += (s, e) => Open(config.RuntimeDirectory);
            ui.Actions["actionStart_HOI4"].Text = "Start enrolled HOI4";
            ui.Actions["actionStart_HOI4"].Click += (s, e) => Guard(() => { using (Process p = ManagerProgram.LaunchGame(config, "manual-" + Guid.NewGuid().ToString("N"))) { } });
            ui.Actions["actionExit"].Text = "Exit manager"; ui.Actions["actionExit"].Click += (s, e) => Exit();
            ui.Actions["actionUpstream_source"].Click += (s, e) => Open("https://github.com/" + releaseSource.injectorRepository);
            ui.Actions["actionModule_source"].Click += (s, e) => Open("https://github.com/" + releaseSource.moduleRepository + "/releases");
            ui.Actions["actionCheck_updates"].Click += (s, e) => updates.RequestCheck();
            ui.Actions["actionCheck_session"].Text = "Check selected player's admission";
            ui.Actions["actionCheck_session"].Click += (s, e) => Guard(() =>
            {
                if (grid.CurrentRow == null) return;
                ManagedGame game = rows.First(g => g.Pid == (int)grid.CurrentRow.Cells[0].Value);
                string player = game.Player.Length > 0 ? game.Player : config.Players[0].Id;
                ShowMessage(Wire.Control(config, "ADMIT|" + config.SessionId + "|" + player + "|" + game.Pid + "|" + game.Creation) + "\r\n\r\n" + game.Detail);
            });
            ui.Actions["actionTroubleshooting"].Click += (s, e) => ShowMessage("Launch the manager before HOI4. It validates the pinned game and DLL, installs the native host join guard and client ticket sender, then verifies the process with the local service.\r\n\r\nPlain clients, stale or changed tickets, and unavailable verification are refused before the host's original join callback. Normal join, hotjoin and reconnect requests are guarded.\r\n\r\nThis service is local. Remote computers cannot obtain tickets yet. One-use tickets do not create separate Steam identities for copies sharing one Steam client.\r\n\r\nNative logs: LOBBY_JOIN_REJECTED / LOBBY_JOIN_ALLOWED. A fresh game is required after switching DLL builds.\r\n\r\nLogs: " + config.RuntimeDirectory);
            ui.Actions["actionShow_version_info"].Click += (s, e) => ShowMessage("HOI4 Injector 0.4.0\r\nGame: 1.19.3.0 | DLL protocol: 2\r\nSession: " + config.SessionId + "\r\n\r\nAutomatic injection and DLL release updates are enabled. Native lobby admission requires a live, one-use verification ticket.");
            ui.Actions["actionEnable_safe_mode"].Text = "Pinned game/DLL verification required"; ui.Actions["actionEnable_safe_mode"].Checked = true; ui.Actions["actionEnable_safe_mode"].Enabled = false;
            ui.Actions["actionHide_when_minimized"].Checked = true; ui.Actions["actionHide_when_minimized"].Click += (s, e) => hideMinimized = ui.Actions["actionHide_when_minimized"].Checked;
            ui.Actions["actionMinimize_on_start"].Enabled = false;
            ui.Actions["actionSelect_game"].Text = "Session configuration is fixed"; ui.Actions["actionSelect_game"].Enabled = false;
            ui.Actions["actionSet_startup_delay"].Text = "Startup delay: " + config.StartupDelayMs + " ms"; ui.Actions["actionSet_startup_delay"].Enabled = false;
            var startup = ui.Actions["actionRun_on_startup"];
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey)) startup.Checked = key != null && (key.GetValue(StartupName) as string) == StartupCommand;
            startup.Click += (s, e) => Guard(() =>
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (startup.Checked) key.SetValue(StartupName, StartupCommand);
                    else if ((key.GetValue(StartupName) as string) == StartupCommand) key.DeleteValue(StartupName, false);
                }
            });
            var monitor = new ToolStripMenuItem("Automatic injection manager") { Checked = true, CheckOnClick = true };
            monitor.Click += (s, e) => engine.Enabled = monitor.Checked;
            ((ToolStripMenuItem)menu.Items[2]).DropDownItems.Insert(0, monitor);
        }
        private async Task Tick()
        {
            if (busy || exiting) return;
            busy = true;
            try
            {
                ManagedGame[] current = await Task.Run(() => { updates.Tick(); return engine.Tick(); });
                LobbyActivitySnapshot activity = await Task.Run(() => LobbyActivity.Read(config, current));
                if (exiting || IsDisposed) return;
                rows = current;
                updateStatus.Text = updates.Status;
                refusedJoin.Text = "Last refused join: " + (activity.Rejected.Length == 0 ? "none recorded" : activity.Rejected);
                allowedJoin.Text = "Last authorized join: " + (activity.Allowed.Length == 0 ? "none recorded" : activity.Allowed);
                grid.Rows.Clear();
                foreach (ManagedGame game in rows) grid.Rows.Add(game.Pid, game.Player.Length > 0 ? game.Player : "-", game.State);
                int verified = rows.Count(g => g.State == "Verified - eligible");
                title.Text = rows.Length == 0 ? "Waiting for HOI4" : verified + " verified / " + rows.Length + " detected";
                if (!engine.Enabled) title.Text = "Automatic injection paused";
                footer.Text = engine.Error.Length == 0 ? "Local verification service | DLL protocol 2 | Session " + config.SessionId.Substring(0, 8) : engine.Error;
                tray.Text = "HOI4 Injector: " + verified + " verified";
            }
            catch (Exception e) { if (!IsDisposed) footer.Text = e.Message; }
            finally { busy = false; }
        }
        private void ShowMessage(string message) { MessageBox.Show(this, message, "HOI4 Injector", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        private void Guard(Action action) { try { action(); } catch (Exception e) { ShowMessage(e.Message); } }
        private void Open(string path) { Guard(() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })); }
        private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        private void Exit() { exiting = true; timer.Stop(); Close(); }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); tray.Visible = false; tray.Dispose(); } base.Dispose(disposing); }
    }
}
