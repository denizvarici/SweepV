using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweepV.App.Services;
using SweepV.Core.Platform;
using SweepV.Core.Ai;
using SweepV.Core.Cleanup;

namespace SweepV.App.ViewModels
{
    public partial class CleanupItemViewModel(CleanupTarget target) : ObservableObject
    {
        public CleanupTarget Target { get; } = target;

        /// <summary>Needs administrator rights the app doesn't currently have.</summary>
        public bool NeedsElevation { get; } = CleanupService.NeedsElevation(target);

        // Admin-only targets would just be skipped, so don't pre-select them without rights.
        [ObservableProperty]
        private bool _isSelected = target.IsRecommended && !CleanupService.NeedsElevation(target);

        [ObservableProperty]
        private long? _sizeInBytes;

        [ObservableProperty]
        private string _status = CleanupService.NeedsElevation(target)
            ? "Requires administrator — restart SweepV as administrator to clean this."
            : string.Empty;

        public string SizeText => SizeInBytes is { } size ? GeminiFolderAdvisor.FormatBytes(size) : "…";
        public bool IsCaution => Target.Risk == CleanupRisk.Caution;

        partial void OnSizeInBytesChanged(long? value) => OnPropertyChanged(nameof(SizeText));
    }

    public partial class CleanupViewModel : ObservableObject
    {
        private readonly CleanupService _service = new();

        public ObservableCollection<CleanupItemViewModel> Items { get; } =
            new(CleanupCatalog.CreateDefault().Select(t => new CleanupItemViewModel(t)));

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CleanCommand), nameof(AnalyzeCommand))]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Click Analyze to measure how much space can be freed.";

        public bool IsElevated => Elevation.IsElevated;

        [RelayCommand]
        private void RestartAsAdmin()
        {
            if (!AdminRelauncher.RestartAsAdministrator())
                StatusMessage = "Administrator restart was cancelled.";
        }

        public string SelectedTotalText =>
            GeminiFolderAdvisor.FormatBytes(Items.Where(i => i.IsSelected).Sum(i => i.SizeInBytes ?? 0));

        public CleanupViewModel()
        {
            foreach (var item in Items)
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(CleanupItemViewModel.IsSelected) or nameof(CleanupItemViewModel.SizeInBytes))
                        OnPropertyChanged(nameof(SelectedTotalText));
                };
        }

        private bool CanRun() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task AnalyzeAsync()
        {
            IsBusy = true;
            StatusMessage = "Measuring…";
            foreach (var item in Items)
                item.SizeInBytes = null;

            await Parallel.ForEachAsync(Items, async (item, ct) =>
            {
                var size = await Task.Run(() => _service.Measure(item.Target, ct), ct);
                App.Current.Dispatcher.Invoke(() => item.SizeInBytes = size);
            });

            var total = Items.Sum(i => i.SizeInBytes ?? 0);
            StatusMessage = $"Found {GeminiFolderAdvisor.FormatBytes(total)} in known cleanup locations.";
            IsBusy = false;
        }

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task CleanAsync()
        {
            var selected = Items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                StatusMessage = "Nothing selected.";
                return;
            }

            var caution = selected.Where(i => i.IsCaution).Select(i => "• " + i.Target.Name).ToList();
            var prompt = "The contents of the selected locations will be permanently deleted.";
            if (caution.Count > 0)
                prompt += "\n\nThese may contain things you want to keep:\n" + string.Join("\n", caution);
            if (!Dialogs.Confirm(prompt + "\n\nContinue?", "Clean selected locations"))
                return;

            IsBusy = true;
            long freed = 0;
            var skipped = 0;
            var needAdmin = new List<string>();
            foreach (var item in selected)
            {
                item.Status = item.Target.RemoveFolderWithOwnership && !item.NeedsElevation
                    ? "Taking ownership and removing… this can take several minutes."
                    : "Cleaning…";
                var result = await Task.Run(() => _service.Clean(item.Target));
                freed += result.FreedBytes;
                skipped += result.SkippedItems;
                if (result.MissingAdminRights)
                    needAdmin.Add(item.Target.Name);

                var freedText = $"Freed {GeminiFolderAdvisor.FormatBytes(result.FreedBytes)}";
                item.Status = result switch
                {
                    { MissingAdminRights: true, FreedBytes: 0 } => "Skipped — requires administrator.",
                    { MissingAdminRights: true } => $"{freedText}; the rest requires administrator.",
                    { SkippedItems: > 0 } => $"{freedText}, {result.SkippedItems} in use / no access",
                    _ => freedText
                };
                item.SizeInBytes = await Task.Run(() => _service.Measure(item.Target));
            }

            StatusMessage = $"Done. Freed {GeminiFolderAdvisor.FormatBytes(freed)}." +
                (skipped > 0 ? $" {skipped} items were in use and skipped." : string.Empty) +
                (needAdmin.Count > 0 ? $" Needs administrator: {string.Join(", ", needAdmin)}." : string.Empty);
            IsBusy = false;
        }
    }
}
