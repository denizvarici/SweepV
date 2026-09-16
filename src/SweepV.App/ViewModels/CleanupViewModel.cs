using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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

        // Nothing is pre-selected: the user decides what gets deleted.
        [ObservableProperty]
        private bool _isSelected;

        /// <summary>Null while measuring.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeInBytes), nameof(SizeText), nameof(IsApplicable), nameof(IsExact), nameof(Note))]
        private TargetInspection? _inspection;

        [ObservableProperty]
        private string _status = CleanupService.NeedsElevation(target)
            ? "Requires administrator — restart SweepV as administrator to clean this."
            : string.Empty;

        public long? SizeInBytes => Inspection?.Bytes;

        /// <summary>Stays visible until measured; hidden when the location/feature doesn't exist on this PC.</summary>
        public bool IsApplicable => Inspection?.IsApplicable ?? true;

        public bool IsExact => Inspection?.IsExact ?? true;
        public string? Note => Inspection?.Note;
        public bool IsCaution => Target.Risk == CleanupRisk.Caution;
        public string Category => Target.Category;

        public string SizeText => Inspection switch
        {
            null => "…",
            { IsExact: true } i => GeminiFolderAdvisor.FormatBytes(i.Bytes),
            { Bytes: > 0 } i => "~" + GeminiFolderAdvisor.FormatBytes(i.Bytes),
            _ => "—"
        };
    }

    public partial class CleanupViewModel : ObservableObject
    {
        private readonly CleanupService _service = new();

        public ObservableCollection<CleanupItemViewModel> Items { get; } =
            new(CleanupCatalog.CreateDefault().Select(t => new CleanupItemViewModel(t)));

        /// <summary>Reversible Windows settings that reserve space (hibernation, Reserved Storage).</summary>
        public IReadOnlyList<SystemToggleViewModel> Toggles { get; } =
            SystemToggles.Create().Where(t => t.IsSupported()).Select(t => new SystemToggleViewModel(t)).ToList();

        /// <summary>
        /// Cleanup items followed by the on/off settings, grouped by category (the settings group comes last),
        /// without items that don't exist on this PC.
        /// </summary>
        public ICollectionView ItemsView { get; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CleanCommand), nameof(AnalyzeCommand))]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Calculating…";

        /// <summary>True while a measurement pass is running.</summary>
        [ObservableProperty]
        private bool _isMeasuring = true;

        /// <summary>Only exact sizes count; estimates (DISM, restore points) would overstate the total.</summary>
        public string TotalText => GeminiFolderAdvisor.FormatBytes(Items.Where(i => i.IsExact).Sum(i => i.SizeInBytes ?? 0));

        public string SelectedTotalText =>
            GeminiFolderAdvisor.FormatBytes(Items.Where(i => i.IsSelected && i.IsExact).Sum(i => i.SizeInBytes ?? 0));

        public bool IsElevated => Elevation.IsElevated;

        public CleanupViewModel()
        {
            // Groups appear in the order of their first item, so appending the toggles puts them at the bottom.
            var rows = new ObservableCollection<object>([.. Items, .. Toggles]);
            ItemsView = CollectionViewSource.GetDefaultView(rows);
            ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupItemViewModel.Category)));
            ItemsView.Filter = row => row is not CleanupItemViewModel item || item.IsApplicable;
            if (ItemsView is ICollectionViewLiveShaping live)
            {
                live.IsLiveFiltering = true;
                live.LiveFilteringProperties.Add(nameof(CleanupItemViewModel.IsApplicable));
            }

            foreach (var item in Items)
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(CleanupItemViewModel.IsSelected) or nameof(CleanupItemViewModel.Inspection))
                        OnPropertyChanged(nameof(SelectedTotalText));
                    if (e.PropertyName is nameof(CleanupItemViewModel.Inspection))
                        OnPropertyChanged(nameof(TotalText));
                };

            // Measure automatically once the window is up.
            App.Current.Dispatcher.BeginInvoke(() => AnalyzeCommand.Execute(null));
        }

        [RelayCommand]
        private void RestartAsAdmin()
        {
            if (!AdminRelauncher.RestartAsAdministrator())
                StatusMessage = "Administrator restart was cancelled.";
        }

        [RelayCommand]
        private void SelectRecommended()
        {
            foreach (var item in Items)
                item.IsSelected = item.IsApplicable && item.Target.IsRecommended && !item.NeedsElevation && item.SizeInBytes is > 0;
        }

        [RelayCommand]
        private void ClearSelection()
        {
            foreach (var item in Items)
                item.IsSelected = false;
        }

        private bool CanRun() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task AnalyzeAsync()
        {
            IsBusy = true;
            IsMeasuring = true;
            StatusMessage = "Calculating…";

            var toggles = Task.WhenAll(Toggles.Select(t => t.RefreshAsync()));
            await Parallel.ForEachAsync(Items, async (item, ct) =>
            {
                var inspection = await Task.Run(() => _service.Inspect(item.Target, ct), ct);
                App.Current.Dispatcher.Invoke(() =>
                {
                    item.Inspection = inspection;
                    if (!inspection.IsApplicable)
                        item.IsSelected = false;
                });
            });
            await toggles;

            IsMeasuring = false;
            StatusMessage = "Select the locations you want to clean.";
            IsBusy = false;
        }

        [RelayCommand(CanExecute = nameof(CanRun))]
        private async Task CleanAsync()
        {
            var selected = Items.Where(i => i.IsSelected && i.IsApplicable).ToList();
            if (selected.Count == 0)
            {
                StatusMessage = "Nothing selected.";
                return;
            }

            var caution = selected.Where(i => i.IsCaution).Select(i => "• " + i.Target.Name).ToList();
            var prompt = "The selected items will be permanently cleaned.";
            if (caution.Count > 0)
                prompt += "\n\nThese may remove things you want to keep:\n" + string.Join("\n", caution);
            if (selected.Any(i => i.Target.Kind == CleanupKind.Command))
                prompt += "\n\nSystem actions can take several minutes.";
            if (!Dialogs.Confirm(prompt + "\n\nContinue?", "Clean selected"))
                return;

            IsBusy = true;
            long freed = 0;
            var skipped = 0;
            var needAdmin = new List<string>();
            foreach (var item in selected)
            {
                item.Status = (item.Target.RemoveFolderWithOwnership || item.Target.Kind == CleanupKind.Command) && !item.NeedsElevation
                    ? "Working… this can take several minutes."
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
                    { Message: { } message } => $"{freedText}. {message}",
                    { SkippedItems: > 0 } => $"{freedText}, {result.SkippedItems} in use / no access",
                    _ => freedText
                };
                var inspection = await Task.Run(() => _service.Inspect(item.Target));
                item.Inspection = inspection;
                item.IsSelected = false;
            }

            StatusMessage = $"Done. Freed {GeminiFolderAdvisor.FormatBytes(freed)}." +
                (skipped > 0 ? $" {skipped} items were in use and skipped." : string.Empty) +
                (needAdmin.Count > 0 ? $" Needs administrator: {string.Join(", ", needAdmin)}." : string.Empty);
            IsBusy = false;
        }
    }
}
