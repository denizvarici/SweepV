namespace SweepV.Core.Models
{
    /// <summary>
    /// represents a single file or directory discovered after scan.
    /// </summary>
    public sealed class ScanNode
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public required bool IsDirectory { get; init; }
        public long SizeInBytes { get; set; }
        public DateTime LastModifiedUtc { get; init; }
        public List<ScanNode> Children { get; init; } = [];


        public double SizeInMb => SizeInBytes / (1024.0 * 1024.0 );
        public double SizeInGb => SizeInBytes / (1024.0 * 1024.0 * 1024.0);
    }
}
