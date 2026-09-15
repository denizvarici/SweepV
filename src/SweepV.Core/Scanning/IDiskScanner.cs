using SweepV.Core.Models;

namespace SweepV.Core.Scanning
{
    /// <summary>
    /// Defines the contract for scanning a directory tree and producing
    /// a ScanNode representation with aggregated sizes.
    /// </summary>
    public interface IDiskScanner
    {
        ScanNode ScanDirectory(string path);
    }
}
