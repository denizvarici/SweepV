using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweepV.Core.Ai;
using SweepV.Core.Cleanup;

namespace SweepV.App.ViewModels
{
    public partial class CleanupItemViewModel(CleanupTarget target) : ObservableObject
    {
        public CleanupTarget Target { get; } = target;

        [ObservableProperty]
        private bool _isSelected = target.IsRecommended;

        [ObservableProperty]
        private long? _sizeInBytes;

        [ObservableProperty]
        private string _status = string.Empty;

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
            {
                item.SizeInBytes = null;
                item.Status = string.Empty;
            }

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
            foreach (var item in selected)
            {
                item.Status = "Cleaning…";
                var result = await Task.Run(() => _service.Clean(item.Target));
                freed += result.FreedBytes;
                skipped += result.SkippedItems;
                item.Status = result.SkippedItems > 0
                    ? $"Freed {GeminiFolderAdvisor.FormatBytes(result.FreedBytes)}, {result.SkippedItems} in use / no access"
                    : $"Freed {GeminiFolderAdvisor.FormatBytes(result.FreedBytes)}";
                item.SizeInBytes = await Task.Run(() => _service.Measure(item.Target));
            }

            StatusMessage = $"Done. Freed {GeminiFolderAdvisor.FormatBytes(freed)}." +
                (skipped > 0 ? $" {skipped} items were skipped (in use or require administrator rights)." : string.Empty);
            IsBusy = false;
        }
    }
}
