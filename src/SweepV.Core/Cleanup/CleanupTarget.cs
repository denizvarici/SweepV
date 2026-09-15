namespace SweepV.Core.Cleanup
{
    public enum CleanupRisk
    {
        /// <summary>Pure cache/temporary data; regenerated automatically.</summary>
        Safe,
        /// <summary>Safe for the system, but may remove something the user wants (downloads, rollback).</summary>
        Caution
    }

    public enum CleanupKind
    {
        /// <summary>Deletes the contents of the resolved folders, keeping the folders themselves.</summary>
        FolderContents,
        /// <summary>Empties the Windows Recycle Bin.</summary>
        RecycleBin
    }

    /// <summary>
    /// A well-known location whose contents can be removed without harming Windows.
    /// </summary>
    public sealed class CleanupTarget
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public CleanupKind Kind { get; init; } = CleanupKind.FolderContents;
        public CleanupRisk Risk { get; init; } = CleanupRisk.Safe;
        public bool IsRecommended { get; init; }
        public bool RequiresAdmin { get; init; }

        /// <summary>
        /// Take ownership before deleting and remove the resolved folders themselves, not just their
        /// contents. Meant for leftovers owned by TrustedInstaller such as Windows.old.
        /// </summary>
        public bool RemoveFolderWithOwnership { get; init; }

        /// <summary>Folders whose contents are cleaned. Non-existent folders are ignored.</summary>
        public Func<IEnumerable<string>> ResolveFolders { get; init; } = () => [];

        /// <summary>Optional file name pattern; when set, only matching files directly in the folder are removed.</summary>
        public string? FilePattern { get; init; }
    }
}
