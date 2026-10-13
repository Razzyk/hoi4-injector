using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace HOI4Admission
{
    // Labels are Windows captions only. No game rendering or Steam API is hooked.
    internal static class WindowLabels
    {
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr parameter,
            string text, uint flags, uint timeout, out UIntPtr result);

        public static void Refresh(Process process, ManagedGame game, Configuration config, bool serviceAvailable)
        {
            try
            {
                process.Refresh();
                if (process.HasExited || Files.Creation(process) != game.Creation ||
                    !string.Equals(process.MainModule.FileName, config.GamePath, StringComparison.OrdinalIgnoreCase)) return;
                IntPtr window = process.MainWindowHandle;
                uint owner;
                if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out owner) == 0 || owner != game.Pid) return;
                bool loaded = process.Modules.Cast<ProcessModule>().Any(module =>
                    string.Equals(module.FileName, config.DllPath, StringComparison.OrdinalIgnoreCase));
                string identity = loaded ? "INJECTED " + game.Player + " " +
                    (serviceAvailable && game.State == "Verified - eligible" ? "VERIFIED" : "UNVERIFIED") : "PLAIN - NO DLL";
                string caption = "[" + identity.Trim() + "] HOI4 - PID " + game.Pid;
                if (process.MainWindowTitle == caption) return;
                UIntPtr result;
                // WM_SETTEXT is marshalled by Windows. Abort promptly if HOI4 is busy.
                SendMessageTimeoutW(window, 0x000C, UIntPtr.Zero, caption, 0x0001 | 0x0002 | 0x0020, 250, out result);
            }
            catch (InvalidOperationException) { /* process/window disappeared */ }
            catch (System.ComponentModel.Win32Exception) { /* inaccessible window/module: leave caption alone */ }
        }
    }
}
