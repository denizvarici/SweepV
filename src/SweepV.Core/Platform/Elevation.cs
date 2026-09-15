using System.Diagnostics;

namespace SweepV.Core.Platform
{
    /// <summary>
    /// Administrator-rights helpers.
    /// </summary>
    public static class Elevation
    {
        public static bool IsElevated => Environment.IsPrivilegedProcess;

        /// <summary>
        /// Makes the Administrators group the owner of <paramref name="path"/> (recursively) and
        /// grants it full control, so protected leftovers like Windows.old can be deleted.
        /// Requires an elevated process. Returns false if either tool failed.
        /// </summary>
        public static bool TakeOwnership(string path, CancellationToken cancellationToken = default)
        {
            if (!IsElevated || !Directory.Exists(path))
                return false;

            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            // *S-1-5-32-544 is the language-independent SID of the Administrators group.
            var owned = Run(Path.Combine(system, "takeown.exe"), ["/F", path, "/R", "/A", "/D", "Y"], cancellationToken);
            var granted = Run(Path.Combine(system, "icacls.exe"), [path, "/grant", "*S-1-5-32-544:F", "/T", "/C", "/Q"], cancellationToken);
            return owned && granted;
        }

        private static bool Run(string exe, string[] args, CancellationToken cancellationToken)
        {
            var info = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process is null)
                return false;

            // Drain output so the child can't block on a full pipe.
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return process.ExitCode == 0;
        }
    }
}
