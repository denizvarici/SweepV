using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweepV.Core.Models;
using SweepV.Core.Scanning;
using System.IO;

namespace SweepV.App.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDiskScanner _scanner = new SimpleDiskScanner();

        [ObservableProperty]
        private string _pathToScan = Path.GetTempPath();

        [ObservableProperty]
        private ScanNode? _scanResult;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        [RelayCommand]
        private async Task ScanAsync()
        {
            StatusMessage = "Scanning...";

            ScanResult = await Task.Run(() => _scanner.ScanDirectory(PathToScan));

            StatusMessage = $"Done. Total size: {ScanResult.SizeInGb:F2} GB, {ScanResult.Children.Count} items";
        }
    }
}
