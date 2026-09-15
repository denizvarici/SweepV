using SweepV.Core.Models;
using SweepV.Core.Platform;
using SweepV.Core.Scanning;

namespace SweepV.Core.Tests
{
    public class ScanEngineTests
    {
        [Fact]
        public void SmartScanner_FallsBackToParallel_WhenFastEngineFails()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);
            File.WriteAllText(Path.Combine(tempPath, "a.txt"), "1234");

            try
            {
                var scanner = new SmartDiskScanner(fast: new FailingScanner());

                var result = scanner.ScanDirectory(tempPath);

                Assert.Equal(ScanEngine.Parallel, scanner.LastEngine);
                Assert.Contains("boom", scanner.LastFallbackReason);
                Assert.Equal(4, result.SizeInBytes);
            }
            finally
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }

        [Fact]
        public void SmartScanner_UsesParallel_ForSubfolders()
        {
            var scanner = new SmartDiskScanner();
            scanner.ScanDirectory(AppContext.BaseDirectory);
            Assert.Equal(ScanEngine.Parallel, scanner.LastEngine);
        }

        [Fact]
        public void ScanNode_DerivesFullPathFromParent_AndKeepsItAfterDetach()
        {
            var root = new ScanNode { Name = @"C:\", FullPath = @"C:\", IsDirectory = true };
            var folder = new ScanNode { Name = "Users", IsDirectory = true, Parent = root };
            var file = new ScanNode { Name = "a.txt", IsDirectory = false, Parent = folder, SizeInBytes = 10, FileCount = 1 };
            root.Children.Add(folder);
            folder.Children.Add(file);

            Assert.Equal(@"C:\Users\a.txt", file.FullPath);
            Assert.False(file.HasChildren);

            file.Detach();
            Assert.Equal(@"C:\Users\a.txt", file.FullPath);
        }

        [Fact]
        public void MftScanner_MatchesParallelScan_OnSystemDrive_WhenElevated()
        {
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
            if (!Elevation.IsElevated || !MftDiskScanner.CanScan(systemRoot))
                return; // Needs an elevated test run.

            var mft = new MftDiskScanner().ScanDirectory(systemRoot);

            Assert.True(mft.FileCount > 10_000);
            var windows = mft.Children.Single(c => c.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase));
            Assert.True(windows.SizeInBytes > 1L << 30);
            Assert.Equal(Path.Combine(systemRoot, "Windows"), windows.FullPath, ignoreCase: true);
        }

        private sealed class FailingScanner : IDiskScanner
        {
            public ScanNode ScanDirectory(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
                throw new IOException("boom");
        }
    }
}
