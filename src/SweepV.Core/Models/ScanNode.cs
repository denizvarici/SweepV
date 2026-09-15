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
        public long FileCount { get; set; }
        public DateTime LastModifiedUtc { get; init; }
        public ScanNode? Parent { get; set; }
        public List<ScanNode> Children { get; init; } = [];


        public double SizeInMb => SizeInBytes / (1024.0 * 1024.0 );
        public double SizeInGb => SizeInBytes / (1024.0 * 1024.0 * 1024.0);

        /// <summary>Share of the parent's size, 0-100.</summary>
        public double PercentOfParent =>
            Parent is { SizeInBytes: > 0 } ? SizeInBytes * 100.0 / Parent.SizeInBytes : 100;

        /// <summary>
        /// Removes this node from its parent and subtracts its size from all ancestors.
        /// </summary>
        public void Detach()
        {
            if (Parent is null)
                return;

            Parent.Children.Remove(this);
            for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                ancestor.SizeInBytes -= SizeInBytes;
                ancestor.FileCount -= IsDirectory ? FileCount : 1;
            }
            Parent = null;
        }
    }
}
