using SweepV.Core.Platform;

namespace SweepV.Core.Cleanup
{
    public readonly record struct CleanupResult(long FreedBytes, int DeletedItems, int SkippedItems);

    /// <summary>
    /// Measures and cleans <see cref="CleanupTarget"/>s. Locked or inaccessible items
    /// are skipped rather than failing the whole run.
    /// </summary>
    public sealed class CleanupService
    {
        private static readonly EnumerationOptions TopLevel = new()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            RecurseSubdirectories = false
        };

        public long Measure(CleanupTarget target, CancellationToken cancellationToken = default)
        {
            if (target.Kind == CleanupKind.RecycleBin)
                return WindowsShell.QueryRecycleBinSize();

            long total = 0;
            foreach (var folder in ExistingFolders(target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += target.FilePattern is null
                    ? MeasureDirectory(new DirectoryInfo(folder), cancellationToken)
                    : MatchingFiles(folder, target.FilePattern).Sum(f => f.Length);
            }
            return total;
        }

        public CleanupResult Clean(CleanupTarget target, CancellationToken cancellationToken = default)
        {
            if (target.Kind == CleanupKind.RecycleBin)
            {
                var size = WindowsShell.QueryRecycleBinSize();
                return WindowsShell.EmptyRecycleBin() ? new CleanupResult(size, 1, 0) : new CleanupResult(0, 0, 1);
            }

            var tally = new Tally();
            foreach (var folder in ExistingFolders(target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.FilePattern is null)
                {
                    DeleteContents(new DirectoryInfo(folder), tally, cancellationToken);
                }
                else
                {
                    foreach (var file in MatchingFiles(folder, target.FilePattern))
                        TryDeleteFile(file, tally);
                }
            }
            return new CleanupResult(tally.Freed, tally.Deleted, tally.Skipped);
        }

        private static IEnumerable<string> ExistingFolders(CleanupTarget target) =>
            target.ResolveFolders()
                  .Where(Directory.Exists)
                  .Select(Path.GetFullPath)
                  .Distinct(StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<FileInfo> MatchingFiles(string folder, string pattern)
        {
            try
            {
                return new DirectoryInfo(folder).EnumerateFiles(pattern, TopLevel).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return [];
            }
        }

        private static IEnumerable<FileSystemInfo> Entries(DirectoryInfo directory)
        {
            try
            {
                return directory.EnumerateFileSystemInfos("*", TopLevel).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return [];
            }
        }

        private static long MeasureDirectory(DirectoryInfo directory, CancellationToken cancellationToken)
        {
            long total = 0;
            foreach (var entry in Entries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is FileInfo file)
                    total += file.Length;
                else if (entry is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    total += MeasureDirectory(sub, cancellationToken);
            }
            return total;
        }

        private static void DeleteContents(DirectoryInfo directory, Tally tally, CancellationToken cancellationToken)
        {
            foreach (var entry in Entries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (entry)
                {
                    case FileInfo file:
                        TryDeleteFile(file, tally);
                        break;

                    case DirectoryInfo sub when sub.Attributes.HasFlag(FileAttributes.ReparsePoint):
                        // Remove the link itself, never what it points to.
                        try { sub.Delete(); tally.Deleted++; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { tally.Skipped++; }
                        break;

                    case DirectoryInfo sub:
                        DeleteContents(sub, tally, cancellationToken);
                        try { sub.Delete(); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still has locked files */ }
                        break;
                }
            }
        }

        private static void TryDeleteFile(FileInfo file, Tally tally)
        {
            try
            {
                var size = file.Length;
                if (file.IsReadOnly)
                    file.IsReadOnly = false;
                file.Delete();
                tally.Freed += size;
                tally.Deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tally.Skipped++;
            }
        }

        private sealed class Tally
        {
            public long Freed;
            public int Deleted;
            public int Skipped;
        }
    }
}
