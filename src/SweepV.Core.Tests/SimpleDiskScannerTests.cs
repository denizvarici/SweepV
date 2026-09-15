using SweepV.Core.Scanning;

namespace SweepV.Core.Tests
{
    public class SimpleDiskScannerTests
    {
        [Fact]
        public void ScanDirectory_EmptyFolder_ReturnsZeroSize()
        {
            //Arrange
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);

            try
            {
                var scanner = new SimpleDiskScanner();

                //Act
                var result = scanner.ScanDirectory(tempPath);

                //Assert
                Assert.True(result.IsDirectory);
                Assert.Equal(0, result.SizeInBytes);
                Assert.Empty(result.Children);
            }
            finally
            {
                Directory.Delete(tempPath);

            }
        }

        [Fact]
        public void ScanDirectory_FolderWithOneFile_CalculatesCorrectSize()
        {
            //Arrange
            var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempPath);

            var filePath = Path.Combine(tempPath, "test.txt");
            var content = "Hello World!"; //12 bytes 
            File.WriteAllText(filePath, content);


            try
            {
                var scanner = new SimpleDiskScanner();

                //Act
                var result = scanner.ScanDirectory(tempPath);

                //Assert
                Assert.Single(result.Children);
                Assert.Equal(content.Length, result.SizeInBytes);
            }
            finally
            {
                File.Delete(filePath);
                Directory.Delete(tempPath);
            }

        }
    }
}
