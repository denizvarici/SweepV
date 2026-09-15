using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweepV.App.Services;
using SweepV.Core.Ai;
using SweepV.Core.Models;
using SweepV.Core.Platform;
using SweepV.Core.Safety;
using SweepV.Core.Scanning;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace SweepV.App.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IDiskScanner _scanner = new SimpleDiskScanner();
        private readonly Stack<ScanNode> _navigationHistory = new();
        private CancellationTokenSource? _scanCts;

        public MainViewModel()
        {
            Drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
            _pathToScan = Drives.FirstOrDefault() ?? Path.GetTempPath();
        }

        public CleanupViewModel Cleanup { get; } = new();
        public AiChatViewModel Chat { get; } = new(new SettingsStore());

        public IReadOnlyList<string> Drives { get; }

        /// <summary>Children of <see cref="CurrentNode"/>, largest first.</summary>
        public ObservableCollection<ScanNode> Items { get; } = [];

        [ObservableProperty]
        private string _pathToScan;

        [ObservableProperty]
        private ScanNode? _rootNode;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
        private ScanNode? _currentNode;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AskAiCommand), nameof(RecycleSelectedCommand), nameof(OpenInExplorerCommand))]
        private ScanNode? _selectedNode;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
        private bool _isScanning;

        [ObservableProperty]
        private string _statusMessage = "Pick a drive or folder and press Scan.";

        public bool CanGoBack => _navigationHistory.Count > 0;

        partial void OnCurrentNodeChanged(ScanNode? value) => RefreshItems();

        private void RefreshItems()
        {
            Items.Clear();
            if (CurrentNode is null)
                return;
            foreach (var child in CurrentNode.Children)
                Items.Add(child);
        }

        private bool CanScan() => !IsScanning;

        [RelayCommand(CanExecute = nameof(CanScan))]
        private async Task ScanAsync()
        {
            if (!Directory.Exists(PathToScan))
            {
                StatusMessage = "Folder not found.";
                return;
            }

            IsScanning = true;
            _scanCts = new CancellationTokenSource();
            _navigationHistory.Clear();
            OnPropertyChanged(nameof(CanGoBack));
            StatusMessage = "Scanning...";

            var progress = new Progress<ScanProgress>(p =>
                StatusMessage = $"Scanning… {p.FilesScanned:N0} files, {GeminiFolderAdvisor.FormatBytes(p.BytesScanned)}  —  {p.CurrentPath}");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var path = PathToScan;
                var token = _scanCts.Token;
                RootNode = await Task.Run(() => _scanner.ScanDirectory(path, progress, token), token);
                CurrentNode = RootNode;
                StatusMessage = $"Done in {stopwatch.Elapsed.TotalSeconds:F1}s. {GeminiFolderAdvisor.FormatBytes(RootNode.SizeInBytes)} in {RootNode.FileCount:N0} files. Double-click a folder to open it.";
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is AggregateException { InnerException: OperationCanceledException })
            {
                StatusMessage = "Scan cancelled.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = "Scan failed: " + ex.Message;
            }
            finally
            {
                IsScanning = false;
            }
        }

        [RelayCommand]
        private void CancelScan() => _scanCts?.Cancel();

        [RelayCommand]
        private void OpenFolder(ScanNode? node)
        {
            if(node is null || !node.IsDirectory)
                return;

            if(CurrentNode is not null)
                _navigationHistory.Push(CurrentNode);

            CurrentNode = node;
            OnPropertyChanged(nameof(CanGoBack));
        }

        [RelayCommand]
        private void GoBack()
        {
            if(_navigationHistory.Count == 0)
                return;

            CurrentNode = _navigationHistory.Pop();
            OnPropertyChanged(nameof(CanGoBack));
        }

        private bool CanGoUp() => CurrentNode?.Parent is not null;

        [RelayCommand(CanExecute = nameof(CanGoUp))]
        private void GoUp() => OpenFolder(CurrentNode?.Parent);

        private bool HasSelection() => SelectedNode is not null;

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private Task AskAiAsync() => SelectedNode is null ? Task.CompletedTask : Chat.StartAsync(SelectedNode);

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private void OpenInExplorer()
        {
            if (SelectedNode is null)
                return;
            var args = SelectedNode.IsDirectory ? $"\"{SelectedNode.FullPath}\"" : $"/select,\"{SelectedNode.FullPath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private async Task RecycleSelectedAsync()
        {
            if (SelectedNode is not { } node)
                return;

            if (PathSafety.IsProtected(node.FullPath, out var reason))
            {
                Dialogs.Info(reason, "Protected location");
                return;
            }

            if (!Dialogs.Confirm($"Move to the Recycle Bin?\n\n{node.FullPath}\n{GeminiFolderAdvisor.FormatBytes(node.SizeInBytes)}\n\nNot sure what it is? Use \"Ask AI\" first.", "Delete"))
                return;

            StatusMessage = $"Moving {node.Name} to the Recycle Bin…";
            var ok = await Task.Run(() => WindowsShell.SendToRecycleBin(node.FullPath));
            var stillExists = node.IsDirectory ? Directory.Exists(node.FullPath) : File.Exists(node.FullPath);
            if (ok && !stillExists)
            {
                node.Detach();
                RefreshItems();
                StatusMessage = $"Moved {node.Name} to the Recycle Bin ({GeminiFolderAdvisor.FormatBytes(node.SizeInBytes)}). Empty the bin in Quick Clean to free the space.";
            }
            else
            {
                StatusMessage = $"Could not delete {node.Name} completely. Some files may be in use or need administrator rights.";
            }
        }
    }
}
