using SweepV.Core.Models;

namespace SweepV.Core.Scanning
{
    /// <summary>
    /// Scans a directory tree using .NET file system APIs and builds a ScanNode tree
    /// with aggregated sizes. Subdirectories near the top of the tree are scanned in
    /// parallel. Reparse points (junctions, symlinks) are not followed so nothing is
    /// counted twice. MFT-based scanning can replace this later for even more speed.
    /// </summary>
    public sealed class SimpleDiskScanner : IDiskScanner
    {
        // Parallelism pays off for the wide top levels; deeper levels stay sequential
        // to avoid flooding the thread pool with tiny tasks.
        private const int ParallelDepth = 3;
        private const int ProgressIntervalMs = 100;

        private static readonly EnumerationOptions Options = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        public ScanNode ScanDirectory(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var directoryInfo = new DirectoryInfo(path);
            if (!directoryInfo.Exists)
                throw new DirectoryNotFoundException($"Directory not found: {path}");

            var state = new ScanState(progress, cancellationToken);
            var root = new ScanNode
            {
                Name = directoryInfo.Name,
                FullPath = directoryInfo.FullName,
                IsDirectory = true,
                LastModifiedUtc = SafeLastWrite(directoryInfo)
            };
            ScanChildrenInto(root, directoryInfo, depth: 0, state);

            progress?.Report(new ScanProgress(state.Files, state.Bytes, root.FullPath));
            return root;
        }

        private static ScanNode CreateDirectoryNode(DirectoryInfo directoryInfo, ScanNode parent) => new()
        {
            Name = directoryInfo.Name,
            IsDirectory = true,
            LastModifiedUtc = SafeLastWrite(directoryInfo),
            Parent = parent
        };

        private static void ScanChildrenInto(ScanNode parent, DirectoryInfo directoryInfo, int depth, ScanState state)
        {
            state.CancellationToken.ThrowIfCancellationRequested();

            var subdirectories = new List<(ScanNode Node, DirectoryInfo Info)>();
            foreach (var entry in SafeEnumerate(directoryInfo))
            {
                if (entry is FileInfo file)
                {
                    var fileNode = new ScanNode()
                    {
                        Name = file.Name,
                        IsDirectory = false,
                        SizeInBytes = file.Length,
                        FileCount = 1,
                        LastModifiedUtc = file.LastWriteTimeUtc,
                        Parent = parent
                    };
                    parent.Children.Add(fileNode);
                    parent.SizeInBytes += fileNode.SizeInBytes;
                    parent.FileCount++;
                    state.AddFile(fileNode.SizeInBytes, directoryInfo);
                }
                else if (entry is DirectoryInfo subdirectory)
                {
                    subdirectories.Add((CreateDirectoryNode(subdirectory, parent), subdirectory));
                }
            }

            if (depth < ParallelDepth && subdirectories.Count > 1)
            {
                Parallel.ForEach(subdirectories,
                    new ParallelOptions { CancellationToken = state.CancellationToken },
                    sub => ScanChildrenInto(sub.Node, sub.Info, depth + 1, state));
            }
            else
            {
                foreach (var sub in subdirectories)
                    ScanChildrenInto(sub.Node, sub.Info, depth + 1, state);
            }

            foreach (var (childNode, _) in subdirectories)
            {
                parent.Children.Add(childNode);
                parent.SizeInBytes += childNode.SizeInBytes;
                parent.FileCount += childNode.FileCount;
            }

            if (parent.HasChildren)
            {
                parent.Children.Sort((a, b) => b.SizeInBytes.CompareTo(a.SizeInBytes));
                parent.Children.TrimExcess();
            }
        }

        private static IEnumerable<FileSystemInfo> SafeEnumerate(DirectoryInfo directoryInfo)
        {
            try
            {
                // Materialize so access errors surface here rather than mid-iteration.
                return directoryInfo.EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                return [];
            }
        }

        private static DateTime SafeLastWrite(DirectoryInfo directoryInfo)
        {
            try
            {
                return directoryInfo.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return default;
            }
        }

        private sealed class ScanState(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        {
            private long _files;
            private long _bytes;
            private long _lastReportTicks;

            public CancellationToken CancellationToken { get; } = cancellationToken;
            public long Files => Interlocked.Read(ref _files);
            public long Bytes => Interlocked.Read(ref _bytes);

            public void AddFile(long size, DirectoryInfo currentDirectory)
            {
                Interlocked.Increment(ref _files);
                Interlocked.Add(ref _bytes, size);

                if (progress is null)
                    return;

                var now = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastReportTicks);
                if (now - last >= ProgressIntervalMs &&
                    Interlocked.CompareExchange(ref _lastReportTicks, now, last) == last)
                {
                    progress.Report(new ScanProgress(Files, Bytes, currentDirectory.FullName));
                }
            }
        }
    }
}
