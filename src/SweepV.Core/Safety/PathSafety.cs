namespace SweepV.Core.Safety
{
    /// <summary>
    /// Guards against deleting locations whose removal would break Windows or wipe a
    /// user's whole profile. This is a last line of defense, not a replacement for
    /// telling the user what a folder is.
    /// </summary>
    public static class PathSafety
    {
        public static bool IsProtected(string path, out string reason)
        {
            reason = string.Empty;
            string full;
            try
            {
                full = Normalize(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                reason = "Invalid path.";
                return true;
            }

            if (Path.GetPathRoot(full) is { } root && string.Equals(Normalize(root), full, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Drive roots cannot be deleted.";
                return true;
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (IsSameOrUnder(full, windows))
            {
                reason = "Files inside the Windows folder are managed by the system. Use Quick Clean or Windows Disk Cleanup instead.";
                return true;
            }

            string[] exactProtected =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty,
            ];
            foreach (var candidate in exactProtected)
            {
                if (!string.IsNullOrEmpty(candidate) && string.Equals(Normalize(candidate), full, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "This is a core system or profile folder and cannot be deleted as a whole.";
                    return true;
                }
            }

            var name = Path.GetFileName(full);
            if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            {
                reason = "This item is managed by Windows. Change the related Windows setting instead of deleting it.";
                return true;
            }

            return false;
        }

        private static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        private static bool IsSameOrUnder(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return false;
            var normalizedFolder = Normalize(folder);
            return path.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
