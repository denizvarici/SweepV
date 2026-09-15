namespace SweepV.Core.Models
{
    /// <summary>
    /// represents a single file or directory discovered after scan.
    /// Kept deliberately small: a full disk scan creates millions of these.
    /// </summary>
    public sealed class ScanNode
    {
        // Only the root (or a detached node) stores its path; everything else derives it from the parent chain.
        private string? _storedPath;
        private List<ScanNode>? _children;

        public required string Name { get; init; }

        public string FullPath
        {
            get => Parent is null ? _storedPath ?? Name : Path.Join(Parent.FullPath, Name);
            init => _storedPath = value;
        }

        public required bool IsDirectory { get; init; }
        public long SizeInBytes { get; set; }
        public long FileCount { get; set; }
        public DateTime LastModifiedUtc { get; init; }
        public ScanNode? Parent { get; set; }

        /// <summary>Allocated on first use, so files don't each carry an empty list.</summary>
        public List<ScanNode> Children => _children ??= [];

        public bool HasChildren => _children is { Count: > 0 };


        public double SizeInMb => SizeInBytes / (1024.0 * 1024.0 );
        public double SizeInGb => SizeInBytes / (1024.0 * 1024.0 * 1024.0);

        /// <summary>Share of the parent's size, 0-100.</summary>
        public double PercentOfParent =>
            Parent is { SizeInBytes: > 0 } ? SizeInBytes * 100.0 / Parent.SizeInBytes : 100;

        /// <summary>Sorts children largest first (recursively, without recursion) and trims list slack.</summary>
        public void SortBySizeRecursive()
        {
            var stack = new Stack<ScanNode>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node._children is null)
                    continue;
                node._children.Sort((a, b) => b.SizeInBytes.CompareTo(a.SizeInBytes));
                node._children.TrimExcess();
                foreach (var child in node._children)
                    if (child.IsDirectory)
                        stack.Push(child);
            }
        }

        /// <summary>
        /// Removes this node from its parent and subtracts its size from all ancestors.
        /// </summary>
        public void Detach()
        {
            if (Parent is null)
                return;

            _storedPath = FullPath;
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
