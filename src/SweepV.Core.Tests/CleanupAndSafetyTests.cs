using SweepV.Core.Cleanup;
using SweepV.Core.Safety;
using SweepV.Core.Scanning;

namespace SweepV.Core.Tests
{
    public class CleanupAndSafetyTests
    {
        [Fact]
        public void Clean_FolderContents_DeletesContentsButKeepsFolder()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path.Combine(tempPath, "sub"));
            File.WriteAllText(Path.Combine(tempPath, "a.txt"), "12345");
            File.WriteAllText(Path.Combine(tempPath, "sub", "b.txt"), "123");

            try
            {
                var target = new CleanupTarget
                {
                    Id = "test",
                    Name = "Test",
                    Description = "Test",
                    ResolveFolders = () => [tempPath]
                };
                var service = new CleanupService();

                Assert.Equal(8, service.Measure(target));
                var result = service.Clean(target);

                Assert.Equal(8, result.FreedBytes);
                Assert.True(Directory.Exists(tempPath));
                Assert.Empty(Directory.EnumerateFileSystemEntries(tempPath));
            }
            finally
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }

        [Fact]
        public void Clean_WithFilePattern_OnlyDeletesMatchingFiles()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);
            File.WriteAllText(Path.Combine(tempPath, "thumbcache_1.db"), "x");
            File.WriteAllText(Path.Combine(tempPath, "keep.db"), "x");

            try
            {
                var target = new CleanupTarget
                {
                    Id = "test",
                    Name = "Test",
                    Description = "Test",
                    ResolveFolders = () => [tempPath],
                    FilePattern = "thumbcache_*.db"
                };

                new CleanupService().Clean(target);

                Assert.False(File.Exists(Path.Combine(tempPath, "thumbcache_1.db")));
                Assert.True(File.Exists(Path.Combine(tempPath, "keep.db")));
            }
            finally
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }

        [Fact]
        public void Clean_AdminOwnershipTargetWithoutElevation_IsSkippedAndReported()
        {
            if (Platform.Elevation.IsElevated)
                return; // Only meaningful for a normal user process.

            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);
            File.WriteAllText(Path.Combine(tempPath, "a.txt"), "x");

            try
            {
                var target = new CleanupTarget
                {
                    Id = "test",
                    Name = "Test",
                    Description = "Test",
                    RequiresAdmin = true,
                    RemoveFolderWithOwnership = true,
                    ResolveFolders = () => [tempPath]
                };

                var result = new CleanupService().Clean(target);

                Assert.True(result.MissingAdminRights);
                Assert.Equal(0, result.FreedBytes);
                Assert.True(File.Exists(Path.Combine(tempPath, "a.txt")));
            }
            finally
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }

        [Fact]
        public void PathSafety_ProtectsSystemLocations()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.True(PathSafety.IsProtected(Path.GetPathRoot(windows)!, out _));
            Assert.True(PathSafety.IsProtected(Path.Combine(windows, "System32"), out _));
            Assert.True(PathSafety.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out _));
            Assert.False(PathSafety.IsProtected(Path.Combine(Path.GetTempPath(), "some-app-cache"), out _));
        }

        [Fact]
        public void Detach_SubtractsSizeFromAncestors()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path.Combine(tempPath, "sub"));
            File.WriteAllText(Path.Combine(tempPath, "sub", "b.txt"), "1234");
            File.WriteAllText(Path.Combine(tempPath, "a.txt"), "12");

            try
            {
                var root = new SimpleDiskScanner().ScanDirectory(tempPath);
                Assert.Equal("sub", root.Children[0].Name); // sorted by size, largest first

                root.Children[0].Detach();

                Assert.Equal(2, root.SizeInBytes);
                Assert.Equal(1, root.FileCount);
                Assert.Single(root.Children);
            }
            finally
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }
}
