using SweepV.Core.Models;

namespace SweepV.Core.Scanning
{
    /// <summary>
    /// Scans a directory tree using standard .NET file system APIs and builds
    /// a ScanNode tree with aggregated sizes. This is the simple, correct-first
    /// implementation — MFT-based scanning can replace this later for speed.
    /// </summary>
    public sealed class SimpleDiskScanner : IDiskScanner
    {
        public ScanNode ScanDirectory(string path)
        {
            var directoryInfo = new DirectoryInfo(path);

            var node = new ScanNode()
            {
                Name = directoryInfo.Name,
                FullPath = directoryInfo.FullName,
                IsDirectory = true,
                LastModifiedUtc = directoryInfo.LastWriteTimeUtc
            };

            ScanChildrenInto(node, directoryInfo);

            return node;
        }

        private void ScanChildrenInto(ScanNode parent, DirectoryInfo directoryInfo)
        {
            //files in directory
            var files = SafeEnumerateFiles(directoryInfo);
            foreach (var file in files)
            {
                var fileNode = new ScanNode()
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    SizeInBytes = file.Length,
                    LastModifiedUtc = file.LastWriteTimeUtc
                };
                parent.Children.Add(fileNode);
                parent.SizeInBytes += fileNode.SizeInBytes;
            }
            //subdirectories
            var subdirectories = SafeEnumerateDirectories(directoryInfo);
            foreach (var subdirectory in subdirectories)
            {
                var childNode = ScanDirectory(subdirectory.FullName);

                parent.Children.Add(childNode);
                parent.SizeInBytes += childNode.SizeInBytes;
            }
        }



        private static IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo directoryInfo)
        {
            try
            {
                return directoryInfo.EnumerateFiles();
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
        }

        private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo directoryInfo)
        {
            try
            {
                return directoryInfo.EnumerateDirectories();
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
        }
    }
}
