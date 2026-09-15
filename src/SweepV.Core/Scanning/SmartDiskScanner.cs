using SweepV.Core.Models;

namespace SweepV.Core.Scanning
{
    public enum ScanEngine { Mft, Parallel }

    /// <summary>
    /// Uses the MFT reader when it can (elevated, NTFS volume root) and falls back to the
    /// parallel directory walker otherwise, or when the MFT read fails for any reason.
    /// </summary>
    public sealed class SmartDiskScanner(IDiskScanner? fast = null, IDiskScanner? fallback = null) : IDiskScanner
    {
        private readonly IDiskScanner _mft = fast ?? new MftDiskScanner();
        private readonly IDiskScanner _parallel = fallback ?? new SimpleDiskScanner();

        public ScanEngine LastEngine { get; private set; } = ScanEngine.Parallel;

        /// <summary>Why the last scan didn't use the MFT engine, if it didn't.</summary>
        public string? LastFallbackReason { get; private set; }

        public ScanNode ScanDirectory(string path, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            LastFallbackReason = null;
            if (fast is not null || MftDiskScanner.CanScan(path))
            {
                try
                {
                    var result = _mft.ScanDirectory(path, progress, cancellationToken);
                    LastEngine = ScanEngine.Mft;
                    return result;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LastFallbackReason = "MFT read failed: " + ex.Message;
                }
            }
            else
            {
                LastFallbackReason = "Fast MFT scan needs administrator rights and a whole NTFS drive (e.g. C:\\).";
            }

            LastEngine = ScanEngine.Parallel;
            return _parallel.ScanDirectory(path, progress, cancellationToken);
        }
    }
}
