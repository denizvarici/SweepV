using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace SweepV.App.Services
{
    internal static class AdminRelauncher
    {
        private const int ErrorCancelled = 1223;

        /// <summary>
        /// Starts a new elevated instance and closes this one. Returns false if the user
        /// declined the UAC prompt, in which case the current instance keeps running.
        /// </summary>
        public static bool RestartAsAdministrator()
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
                return false;

            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return false;
            }

            Application.Current.Shutdown();
            return true;
        }
    }
}
