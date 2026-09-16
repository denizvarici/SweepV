using SweepV.Core.Platform;

namespace SweepV.Core.Cleanup
{
    /// <summary>Current state of a Windows feature that reserves disk space.</summary>
    /// <param name="IsOn">Null when the state can't be read (usually missing admin rights).</param>
    /// <param name="SpaceBytes">Space the feature uses (or would give back when turned off).</param>
    /// <param name="IsExact">False when <paramref name="SpaceBytes"/> is a typical value rather than measured.</param>
    public readonly record struct ToggleState(bool? IsOn, long SpaceBytes, bool IsExact = true, string? Note = null)
    {
        public static ToggleState Unavailable => new(null, 0, Note: "Not available on this PC.");
    }

    /// <summary>
    /// A reversible Windows setting that trades a feature for disk space
    /// (unlike <see cref="SystemActions"/>, which can't be undone).
    /// </summary>
    public sealed class SystemToggle
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }

        /// <summary>What the user gives up while it's off.</summary>
        public required string OffConsequence { get; init; }

        public bool RequiresAdmin { get; init; } = true;

        /// <summary>Whether the feature exists at all on this PC; hidden in the UI otherwise.</summary>
        public Func<bool> IsSupported { get; init; } = () => true;

        public required Func<CancellationToken, ToggleState> Query { get; init; }
        public required Func<bool, CancellationToken, ProcessResult> Set { get; init; }
    }

    public static class SystemToggles
    {
        private const long TypicalReservedStorage = 7L * 1024 * 1024 * 1024;

        public static IReadOnlyList<SystemToggle> Create() => [Hibernation(), ReservedStorage()];

        private static SystemToggle Hibernation() => new()
        {
            Id = "hibernation",
            Name = "Hibernation & Fast Startup (hiberfil.sys)",
            Description = "Windows keeps a hibernation file on C: that is often 40% of your RAM.",
            OffConsequence = "Hibernate and Fast Startup stop working; laptops that hibernate on low battery will shut down instead.",
            Query = _ =>
            {
                // The file only exists while hibernation is on, and its size is readable without admin rights.
                var file = SystemActions.SystemDriveFile("hiberfil.sys");
                return file is null
                    ? new ToggleState(false, 0, Note: "Turning it on creates the file again.")
                    : new ToggleState(true, file.Length);
            },
            Set = (on, ct) => ProcessRunner.Run(ProcessRunner.SystemTool("powercfg.exe"), ["/hibernate", on ? "on" : "off"], ct)
        };

        private static SystemToggle ReservedStorage() => new()
        {
            Id = "reserved-storage",
            Name = "Reserved Storage",
            Description = "Windows sets aside about 7 GB so updates always have room to install.",
            OffConsequence = "Large Windows updates may fail when the disk is nearly full.",
            // Reserved Storage was introduced in Windows 10 1903 (build 18362).
            IsSupported = () => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362),
            Query = ct =>
            {
                if (!Elevation.IsElevated)
                    return new ToggleState(null, TypicalReservedStorage, IsExact: false, Note: "Restart as administrator to see whether it is on.");

                var result = ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"), ["/Online", "/Get-ReservedStorageState", "/English"], ct);
                if (!result.Succeeded)
                    return new ToggleState(null, TypicalReservedStorage, IsExact: false, Note: "Windows could not report the state right now.");

                var isOn = result.Output.Contains("Reserved storage is enabled", StringComparison.OrdinalIgnoreCase);
                return new ToggleState(isOn, TypicalReservedStorage, IsExact: false,
                    Note: isOn ? null : "Windows may keep it off until pending updates finish.");
            },
            Set = (on, ct) => ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"),
                ["/Online", "/Set-ReservedStorageState", on ? "/State:Enabled" : "/State:Disabled", "/English"], ct)
        };
    }
}
