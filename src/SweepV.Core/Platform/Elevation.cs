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

            // *S-1-5-32-544 is the language-independent SID of the Administrators group.
            var owned = ProcessRunner.Run(ProcessRunner.SystemTool("takeown.exe"), ["/F", path, "/R", "/A", "/D", "Y"], cancellationToken);
            var granted = ProcessRunner.Run(ProcessRunner.SystemTool("icacls.exe"), [path, "/grant", "*S-1-5-32-544:F", "/T", "/C", "/Q"], cancellationToken);
            return owned.Succeeded && granted.Succeeded;
        }
    }
}
