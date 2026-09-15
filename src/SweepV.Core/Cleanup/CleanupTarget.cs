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
        RecycleBin,
        /// <summary>Runs a Windows tool (DISM, powercfg, ...) via <see cref="CleanupTarget.Inspect"/> / <see cref="CleanupTarget.Execute"/>.</summary>
        Command
    }

    /// <summary>What a target looks like on this PC.</summary>
    /// <param name="IsApplicable">False when the thing doesn't exist here at all (app not installed, feature off); the UI hides it.</param>
    /// <param name="Bytes">Space that can be freed.</param>
    /// <param name="IsExact">False for estimates (e.g. compacting a virtual disk); estimates aren't added to totals.</param>
    /// <param name="Note">Extra context shown under the item.</param>
    public readonly record struct TargetInspection(bool IsApplicable, long Bytes, bool IsExact = true, string? Note = null)
    {
        public static TargetInspection NotApplicable => new(false, 0);
    }

    public static class CleanupCategories
    {
        public const string Windows = "Windows";
        public const string UpgradeLeftovers = "Windows upgrade & driver leftovers";
        public const string BrowsersAndApps = "Browsers & apps";
        public const string Gaming = "Games & graphics";
        public const string Developer = "Developer caches";
        public const string Personal = "Personal files";
        public const string SystemActions = "System actions";
    }

    /// <summary>
    /// A well-known location whose contents can be removed without harming Windows.
    /// </summary>
    public sealed class CleanupTarget
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public string Category { get; init; } = CleanupCategories.Windows;
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

        /// <summary><see cref="CleanupKind.Command"/> only: checks applicability and reclaimable space.</summary>
        public Func<CancellationToken, TargetInspection>? Inspect { get; init; }

        /// <summary><see cref="CleanupKind.Command"/> only: performs the action.</summary>
        public Func<CancellationToken, CleanupResult>? Execute { get; init; }
    }
}
