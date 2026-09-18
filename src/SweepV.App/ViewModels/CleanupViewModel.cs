using System.Collections.Concurrent;
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

        /// <summary>Result of the quick existence check; null until checked (or for commands, which can't be checked cheaply).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsApplicable))]
        private bool? _isPresent;

        public long? SizeInBytes => Inspection?.Bytes;

        /// <summary>Bytes this item adds to totals: exact sizes only, estimates would overstate them.</summary>
        public long CountedBytes => Counted(Inspection);

        private static long Counted(TargetInspection? inspection) => inspection is { IsExact: true } i ? i.Bytes : 0;

        /// <summary>Raised with the change in <see cref="CountedBytes"/>, so totals can be kept incrementally.</summary>
        public event Action<CleanupItemViewModel, long>? CountedBytesChanged;

        public event Action<CleanupItemViewModel>? SelectionChanged;

        partial void OnInspectionChanged(TargetInspection? oldValue, TargetInspection? newValue)
        {
            var delta = Counted(newValue) - Counted(oldValue);
            if (delta != 0)
                CountedBytesChanged?.Invoke(this, delta);
        }

        partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

        /// <summary>
        /// Hidden when the location/feature doesn't exist on this PC. Folder items appear once the existence
        /// check passes; commands show while being inspected.
        /// </summary>
        public bool IsApplicable => Inspection?.IsApplicable ?? IsPresent ?? Target.Kind == CleanupKind.Command;

        public bool IsExact => Inspection?.IsExact ?? true;
        public string? Note => Inspection?.Note;
        public bool IsCaution => Target.Risk == CleanupRisk.Caution;
        public string Category => Target.Category;

        /// <summary>System actions are run one at a time with their own button, not ticked in a list.</summary>
        public bool IsAction => Target.Kind == CleanupKind.Command;

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
        private string _statusMessage = "Calculating…";

        /// <summary>True while a measurement pass is running. Cleaning is still allowed meanwhile.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
        private bool _isMeasuring = true;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CleanCommand), nameof(AnalyzeCommand), nameof(RunActionCommand))]
        private bool _isCleaning;

        /// <summary>Items being cleaned right now; a measurement finishing must not overwrite their state.</summary>
        private readonly HashSet<CleanupItemViewModel> _cleaningItems = [];

        // Totals are kept incrementally (O(1) per change) instead of re-summing every item on each update.
        private long _totalBytes;
        private long _selectedBytes;
        private int _batchDepth;

        /// <summary>Only exact sizes count; estimates (DISM, restore points) would overstate the total.</summary>
        public string TotalText => GeminiFolderAdvisor.FormatBytes(_totalBytes);

        public string SelectedTotalText => GeminiFolderAdvisor.FormatBytes(_selectedBytes);

        public bool IsElevated => Elevation.IsElevated;

        public CleanupViewModel()
        {
            // Groups appear in the order of their first item, so appending the toggles puts them at the bottom.
            var rows = new ObservableCollection<object>([.. Items, .. Toggles]);
            ItemsView = CollectionViewSource.GetDefaultView(rows);
            ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupItemViewModel.Category)));
            // No live filtering: visibility changes are applied in batches followed by a single Refresh().
            ItemsView.Filter = row => row is not CleanupItemViewModel item || item.IsApplicable;

            foreach (var item in Items)
            {
                item.CountedBytesChanged += (changed, delta) =>
                {
                    _totalBytes += delta;
                    if (changed.IsSelected)
                        _selectedBytes += delta;
                    NotifyTotals();
                };
                item.SelectionChanged += changed =>
                {
                    _selectedBytes += changed.IsSelected ? changed.CountedBytes : -changed.CountedBytes;
                    NotifyTotals();
                };
            }

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
        private void SelectRecommended() => ApplyInBatch(() =>
        {
            foreach (var item in Items)
                item.IsSelected = item.IsApplicable && !item.IsAction && item.Target.IsRecommended &&
                                  !item.NeedsElevation && item.SizeInBytes is > 0;
            return false;
        });

        [RelayCommand]
        private void ClearSelection() => ApplyInBatch(() =>
        {
            foreach (var item in Items)
                item.IsSelected = false;
            return false;
        });

        private void NotifyTotals()
        {
            if (_batchDepth > 0)
                return;
            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(SelectedTotalText));
        }

        /// <summary>
        /// Applies many item changes at once: total notifications are raised once at the end, and the
        /// grouped list is refreshed once — only if <paramref name="changes"/> reports that visibility changed.
        /// </summary>
        private void ApplyInBatch(Func<bool> changes)
        {
            _batchDepth++;
            var visibilityChanged = true;
            try
            {
                visibilityChanged = changes();
            }
            finally
            {
                _batchDepth--;
                if (visibilityChanged)
                    ItemsView.Refresh();
                NotifyTotals();
            }
        }

        /// <summary>
        /// Measuring is mostly disk I/O; a few workers keep throughput high without making the disk
        /// (especially an HDD) seek back and forth between dozens of folders at once.
        /// </summary>
        private static int MeasureParallelism => Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

        private void ApplyMeasurements(ConcurrentQueue<(CleanupItemViewModel Item, TargetInspection Inspection)> results)
        {
            if (results.IsEmpty)
                return;

            ApplyInBatch(() =>
            {
                var visibilityChanged = false;
                while (results.TryDequeue(out var result))
                {
                    // A stale size would overwrite the "Cleaning…" status and the fresh result.
                    if (_cleaningItems.Contains(result.Item))
                        continue;

                    var wasVisible = result.Item.IsApplicable;
                    result.Item.Inspection = result.Inspection;
                    if (!result.Inspection.IsApplicable)
                        result.Item.IsSelected = false;
                    visibilityChanged |= wasVisible != result.Item.IsApplicable;
                }
                return visibilityChanged;
            });
        }

        private bool CanAnalyze() => !IsMeasuring && !IsCleaning;

        /// <summary>Cleaning may start while sizes are still being calculated.</summary>
        private bool CanClean() => !IsCleaning;

        [RelayCommand(CanExecute = nameof(CanAnalyze))]
        private async Task AnalyzeAsync()
        {
            IsMeasuring = true;
            StatusMessage = "Calculating…";

            var toggles = Task.WhenAll(Toggles.Select(t => t.RefreshAsync()));

            // Phase 1: cheap existence checks only, then build the visible list in a single refresh.
            var presence = await Task.Run(() => Items.AsParallel().AsOrdered().Select(i => _service.IsPresent(i.Target)).ToArray());
            ApplyInBatch(() =>
            {
                for (var i = 0; i < Items.Count; i++)
                {
                    Items[i].Inspection = null;
                    Items[i].IsPresent = presence[i];
                    if (presence[i] == false)
                        Items[i].IsSelected = false;
                }
                return true;
            });

            // Phase 2: measure sizes, but only for things that exist here (commands decide during inspection).
            // Workers queue results; the UI thread applies whatever arrived every 100 ms as one batch.
            var toMeasure = Items.Where(i => i.IsPresent != false).ToList();
            var results = new ConcurrentQueue<(CleanupItemViewModel Item, TargetInspection Inspection)>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = MeasureParallelism };
            var measuring = Task.Run(() => Parallel.ForEach(toMeasure, options, item =>
                results.Enqueue((item, _service.Inspect(item.Target)))));

            using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100)))
            {
                while (!measuring.IsCompleted)
                {
                    await timer.WaitForNextTickAsync();
                    ApplyMeasurements(results);
                }
            }
            await measuring;
            ApplyMeasurements(results);
            await toggles;

            IsMeasuring = false;
            if (!IsCleaning)
                StatusMessage = "Select the locations you want to clean.";
        }

        [RelayCommand(CanExecute = nameof(CanClean))]
        private async Task CleanAsync()
        {
            // System actions are not tickable; each has its own Run button.
            var selected = Items.Where(i => i.IsSelected && i.IsApplicable && !i.IsAction).ToList();
            if (selected.Count == 0)
            {
                StatusMessage = "Nothing selected.";
                return;
            }

            // Cleaning something whose size is unknown would report a wrong result, so wait for it.
            var pending = selected.Where(i => i.Inspection is null).Select(i => i.Target.Name).ToList();
            if (pending.Count > 0)
            {
                StatusMessage = $"Still calculating: {string.Join(", ", pending)}. Wait until they show a size, or untick them.";
                return;
            }

            var caution = selected.Where(i => i.IsCaution).Select(i => "• " + i.Target.Name).ToList();
            var prompt = "The selected items will be permanently cleaned.";
            if (caution.Count > 0)
                prompt += "\n\nThese may remove things you want to keep:\n" + string.Join("\n", caution);
            if (!Dialogs.Confirm(prompt + "\n\nContinue?", "Clean selected"))
                return;

            IsCleaning = true;
            long freed = 0;
            var skipped = 0;
            var needAdmin = new List<string>();
            foreach (var item in selected)
            {
                var result = await CleanItemAsync(item);
                freed += result.FreedBytes;
                skipped += result.SkippedItems;
                if (result.MissingAdminRights)
                    needAdmin.Add(item.Target.Name);
                item.IsSelected = false;
            }
            // Cleaning can make a location disappear (e.g. Windows.old); update visibility once.
            ItemsView.Refresh();

            StatusMessage = $"Done. Freed {GeminiFolderAdvisor.FormatBytes(freed)}." +
                (skipped > 0 ? $" {skipped} items were in use and skipped." : string.Empty) +
                (needAdmin.Count > 0 ? $" Needs administrator: {string.Join(", ", needAdmin)}." : string.Empty);
            IsCleaning = false;
        }

        /// <summary>Runs one system action on its own, from the Run button next to it.</summary>
        [RelayCommand(CanExecute = nameof(CanClean))]
        private async Task RunActionAsync(CleanupItemViewModel? item)
        {
            if (item is null)
                return;
            if (item.Inspection is null)
            {
                StatusMessage = $"Still calculating {item.Target.Name}.";
                return;
            }
            if (item.NeedsElevation)
            {
                StatusMessage = $"{item.Target.Name} needs administrator rights — restart SweepV as administrator.";
                return;
            }

            var prompt = $"{item.Target.Name}\n\n{item.Target.Description}";
            if (item.Note is { } note)
                prompt += "\n\n" + note;
            prompt += "\n\nThis can take several minutes. Leave SweepV open until it finishes.\n\nRun it now?";
            if (!Dialogs.Confirm(prompt, "Run system action"))
                return;

            IsCleaning = true;
            var result = await CleanItemAsync(item);
            ItemsView.Refresh();
            StatusMessage = $"{item.Target.Name}: freed {GeminiFolderAdvisor.FormatBytes(result.FreedBytes)}." +
                (result.Message is { } message ? " " + message : string.Empty);
            IsCleaning = false;
        }

        /// <summary>Cleans one item and updates its row: status, new size, and nothing overwritten meanwhile.</summary>
        private async Task<CleanupResult> CleanItemAsync(CleanupItemViewModel item)
        {
            _cleaningItems.Add(item);
            item.Status = (item.Target.RemoveFolderWithOwnership || item.IsAction) && !item.NeedsElevation
                ? "Working… this can take several minutes. Leave SweepV open."
                : "Cleaning…";

            var result = await Task.Run(() => _service.Clean(item.Target));

            var freedText = $"Freed {GeminiFolderAdvisor.FormatBytes(result.FreedBytes)}";
            item.Status = result switch
            {
                { MissingAdminRights: true, FreedBytes: 0 } => "Skipped — requires administrator.",
                { MissingAdminRights: true } => $"{freedText}; the rest requires administrator.",
                { Message: { } message } => $"{freedText}. {message}",
                { SkippedItems: > 0 } => $"{freedText}, {result.SkippedItems} in use / no access",
                _ => freedText
            };
            item.Inspection = await Task.Run(() => _service.Inspect(item.Target));
            _cleaningItems.Remove(item);
            return result;
        }
    }
}
