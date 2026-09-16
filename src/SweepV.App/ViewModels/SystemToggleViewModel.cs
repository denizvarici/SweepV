using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweepV.Core.Ai;
using SweepV.Core.Cleanup;
using SweepV.Core.Platform;

namespace SweepV.App.ViewModels
{
    /// <summary>One on/off Windows setting that reserves disk space.</summary>
    public partial class SystemToggleViewModel(SystemToggle toggle) : ObservableObject
    {
        public SystemToggle Toggle { get; } = toggle;

        public const string GroupName = "Space-saving settings (turn on/off any time)";

        /// <summary>Group shown at the bottom of the Quick Clean list.</summary>
        public string Category => GroupName;

        public bool NeedsElevation => Toggle.RequiresAdmin && !Elevation.IsElevated;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText), nameof(ActionText), nameof(SpaceText), nameof(Note), nameof(IsOn))]
        [NotifyCanExecuteChangedFor(nameof(FlipCommand))]
        private ToggleState? _state;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateText))]
        [NotifyCanExecuteChangedFor(nameof(FlipCommand))]
        private bool _isWorking;

        [ObservableProperty]
        private string? _result;

        public bool? IsOn => State?.IsOn;

        public string StateText => IsWorking ? "Working…" : State?.IsOn switch
        {
            true => "On",
            false => "Off",
            null => State is null ? "Checking…" : "Unknown"
        };

        public string ActionText => State?.IsOn == true ? "Turn off" : "Turn on";

        public string SpaceText => State switch
        {
            { IsOn: true, SpaceBytes: > 0 } s => $"Uses {(s.IsExact ? "" : "~")}{GeminiFolderAdvisor.FormatBytes(s.SpaceBytes)}",
            { IsOn: null, SpaceBytes: > 0 } s => $"Typically ~{GeminiFolderAdvisor.FormatBytes(s.SpaceBytes)}",
            _ => string.Empty
        };

        public string? Note => NeedsElevation && State?.IsOn is not null
            ? "Restart as administrator to change this."
            : State?.Note;

        public async Task RefreshAsync()
        {
            State = await Task.Run(() => Toggle.Query(CancellationToken.None));
        }

        private bool CanFlip() => !IsWorking && !NeedsElevation && State?.IsOn is not null;

        [RelayCommand(CanExecute = nameof(CanFlip))]
        private async Task FlipAsync()
        {
            var turnOn = State?.IsOn != true;
            if (!turnOn && !Dialogs.Confirm($"Turn off {Toggle.Name}?\n\n{Toggle.OffConsequence}\n\nYou can turn it back on here at any time.", "Turn off"))
                return;

            IsWorking = true;
            Result = null;
            try
            {
                var outcome = await Task.Run(() => Toggle.Set(turnOn, CancellationToken.None));
                await RefreshAsync();
                Result = outcome.Succeeded
                    ? $"Turned {(turnOn ? "on" : "off")}."
                    : "Windows reported an error: " + SystemActions.LastLine(outcome.Output);
            }
            finally
            {
                IsWorking = false;
            }
        }
    }
}
