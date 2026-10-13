using System.Collections.Generic;
using System.Windows.Forms;

namespace HOI4Manager
{
    // Authored Windows Forms menus; no upstream UI resource is redistributed.
    public sealed class NativeMenu
    {
        public readonly Dictionary<string, ToolStripMenuItem> Actions = new Dictionary<string, ToolStripMenuItem>();
        private void Add(ToolStripMenuItem menu, string id, string text, bool check)
        {
            var action = new ToolStripMenuItem(text) { CheckOnClick = check };
            Actions.Add(id, action); menu.DropDownItems.Add(action);
        }
        public MenuStrip BuildMenu()
        {
            var result = new MenuStrip();
            var file = new ToolStripMenuItem("File");
            Add(file, "actionStart_HOI4", "Start HOI4", false);
            Add(file, "actionOpen_manager_folder", "Open session folder", false);
            Add(file, "actionExit", "Exit", false);
            var links = new ToolStripMenuItem("Links");
            Add(links, "actionUpstream_source", "Injector on GitHub", false);
            Add(links, "actionModule_source", "DLL releases", false);
            var settings = new ToolStripMenuItem("Settings");
            Add(settings, "actionRun_on_startup", "Run on Windows sign-in", true);
            Add(settings, "actionHide_when_minimized", "Hide when minimized", true);
            Add(settings, "actionMinimize_on_start", "Minimize on start", true);
            Add(settings, "actionEnable_safe_mode", "Require supported game build", true);
            Add(settings, "actionSelect_game", "Game location", false);
            Add(settings, "actionSet_startup_delay", "Startup delay", false);
            var help = new ToolStripMenuItem("Help");
            Add(help, "actionCheck_updates", "Check for DLL updates", false);
            Add(help, "actionCheck_session", "Check selected player", false);
            Add(help, "actionTroubleshooting", "Help", false);
            Add(help, "actionShow_version_info", "About", false);
            result.Items.AddRange(new ToolStripItem[] { file, links, settings, help });
            return result;
        }
    }
}
