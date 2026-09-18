using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SweepV.Core.Platform;

namespace SweepV.Core.Cleanup
{
    /// <summary>
    /// Space savers that go through Windows tools instead of deleting folders.
    /// Each one reports itself as not applicable when the feature isn't present on the PC.
    /// </summary>
    public static partial class SystemActions
    {
        public static IReadOnlyList<CleanupTarget> Create() =>
        [
            ComponentStoreCleanup(),
            ComponentStoreResetBase(),
            RestorePoints(),
            VirtualDiskCompaction()
        ];

        // ------------------------------------------------------------ WinSxS

        private static CleanupTarget ComponentStoreCleanup() => new()
        {
            Id = "component-store",
            Name = "Clean up Windows component store (WinSxS)",
            Description = "Removes superseded component versions and cached data left by updates, using DISM. Safe, but can take 10+ minutes. Note: Explorer shows WinSxS as much larger than it is, because most of it is shared with Windows itself.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            IsRecommended = true,
            RequiresAdmin = true,
            Inspect = ct =>
            {
                if (!Elevation.IsElevated)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Restart as administrator to see how much can be freed.");

                var analysis = AnalyzeComponentStore(ct);
                if (analysis is not { } store)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Windows could not analyze the component store right now.");

                // Only the cache/temp part is actually removed by StartComponentCleanup; the backups
                // need /ResetBase (a separate item) and disabled features need the feature removed.
                var note = store.Recommended
                    ? "Windows recommends running this cleanup."
                    : "Windows says a cleanup isn't needed right now.";
                if (store.Backups > 0)
                    note += $" A further {FormatGb(store.Backups)} sits in update backups — see the /ResetBase item below.";

                return new TargetInspection(true, store.CacheAndTemp, IsExact: false, Note: note);
            },
            Execute = ct => RunAndMeasureComponentStore(ct, ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English"])
        };

        private static CleanupTarget ComponentStoreResetBase() => new()
        {
            Id = "component-store-resetbase",
            Name = "Remove update backups from the component store (/ResetBase)",
            Description = "Deletes the backed-up versions of every Windows update installed so far. This is where the big WinSxS savings are.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            Risk = CleanupRisk.Caution,
            RequiresAdmin = true,
            Inspect = ct =>
            {
                if (!Elevation.IsElevated)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Restart as administrator to see how much can be freed.");

                var analysis = AnalyzeComponentStore(ct);
                if (analysis is not { } store)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Windows could not analyze the component store right now.");
                if (store.Backups <= 0)
                    return TargetInspection.NotApplicable;

                return new TargetInspection(true, store.Backups, IsExact: false,
                    Note: "After this, already installed Windows updates can no longer be uninstalled.");
            },
            Execute = ct => RunAndMeasureComponentStore(ct, ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase", "/English"])
        };

        private readonly record struct ComponentStoreAnalysis(long Backups, long CacheAndTemp, bool Recommended)
        {
            public long Reclaimable => Backups + CacheAndTemp;
        }

        private static ComponentStoreAnalysis? AnalyzeComponentStore(CancellationToken ct)
        {
            // /English keeps the output parseable on localized Windows.
            var result = ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"),
                ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore", "/English"], ct);
            if (!result.Succeeded)
                return null;

            return new ComponentStoreAnalysis(
                ParseLabeledSize(result.Output, "Backups and Disabled Features"),
                ParseLabeledSize(result.Output, "Cache and Temporary Data"),
                Regex.IsMatch(result.Output, @"Component Store Cleanup Recommended\s*:\s*Yes", RegexOptions.IgnoreCase));
        }

        /// <summary>
        /// Runs a DISM cleanup and reports how much DISM itself says is no longer reclaimable.
        /// Free disk space is unreliable here: Windows defers part of the work and other apps keep writing.
        /// </summary>
        private static CleanupResult RunAndMeasureComponentStore(CancellationToken ct, string[] args)
        {
            var before = AnalyzeComponentStore(ct);
            var result = ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"), args, ct);
            var after = AnalyzeComponentStore(ct);

            if (!result.Succeeded)
                return new CleanupResult(0, 0, 1, Message: "Windows reported an error: " + LastLine(result.Output));
            if (before is not { } b || after is not { } a)
                return new CleanupResult(0, 1, 0, Message: "Done, but Windows could not report the new size.");

            var freed = Math.Max(0, b.Reclaimable - a.Reclaimable);
            return new CleanupResult(freed, 1, 0,
                Message: freed == 0 ? "Windows had nothing left to remove here." : null);
        }

        // ------------------------------------------------------------ restore points

        private static CleanupTarget RestorePoints() => new()
        {
            Id = "restore-points",
            Name = "Delete older restore points (opens Windows)",
            Description = "System Restore points can take many GB. SweepV opens the Windows System Protection dialog so you can delete them yourself: pick your drive, choose Configure, then Delete.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            Risk = CleanupRisk.Caution,
            RequiresAdmin = true,
            Inspect = ct =>
            {
                if (!Elevation.IsElevated)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Restart as administrator to check restore points.");

                var count = CountShadowCopies(ct);
                if (count == 0)
                    return TargetInspection.NotApplicable;

                return new TargetInspection(true, UsedShadowStorage(ct), IsExact: false,
                    Note: $"{count} restore point(s) use this space. Windows deletes all but the newest one.");
            },
            // Deleting shadow copies ourselves (vssadmin delete shadows) is exactly what ransomware does,
            // so antivirus software blocks it. Hand the job to Windows' own dialog instead.
            Execute = ct => OpenWindowsDialog(ct, "SystemPropertiesProtection.exe",
                "System Protection opened — use Configure → Delete, then press Recalculate here.")
        };

        /// <summary>Opens a Windows dialog and waits for the user to close it, then reports the space freed.</summary>
        private static CleanupResult OpenWindowsDialog(CancellationToken ct, string tool, string message)
        {
            var before = new DriveInfo(SystemDrive()).AvailableFreeSpace;
            using var process = Process.Start(new ProcessStartInfo(ProcessRunner.SystemTool(tool)) { UseShellExecute = true });
            if (process is null)
                return new CleanupResult(0, 0, 1, Message: $"Could not open {tool}.");

            process.WaitForExitAsync(ct).GetAwaiter().GetResult();
            var freed = Math.Max(0, new DriveInfo(SystemDrive()).AvailableFreeSpace - before);
            return new CleanupResult(freed, 1, 0, Message: message);
        }

        private static int CountShadowCopies(CancellationToken ct)
        {
            var result = ProcessRunner.Run(ProcessRunner.SystemTool("vssadmin.exe"), ["list", "shadows", $"/for={SystemDrive().TrimEnd('\\')}"], ct);
            // Each copy lists its device path, which isn't localized (unlike the labels around it).
            return result.Succeeded ? ShadowVolumeRegex().Matches(result.Output).Count : 0;
        }

        private static long UsedShadowStorage(CancellationToken ct)
        {
            var result = ProcessRunner.Run(ProcessRunner.SystemTool("vssadmin.exe"), ["list", "shadowstorage", $"/for={SystemDrive().TrimEnd('\\')}"], ct);
            // Lines are Used / Allocated / Maximum in that order; the first size is "used".
            var match = SizeRegex().Match(result.Output);
            return match.Success ? ToBytes(match.Groups[1].Value, match.Groups[2].Value) : 0;
        }

        // ------------------------------------------------------------ WSL / Docker disks

        private static CleanupTarget VirtualDiskCompaction() => new()
        {
            Id = "vhdx-compact",
            Name = "Compact WSL / Docker virtual disks",
            Description = "Linux (WSL) and Docker Desktop disks grow but never shrink. This shuts down WSL and compacts them to release unused space. Quit Docker Desktop first; run 'docker system prune' before for bigger savings.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            IsRecommended = true,
            RequiresAdmin = true,
            Inspect = _ =>
            {
                var disks = FindVirtualDisks().ToList();
                if (disks.Count == 0)
                    return TargetInspection.NotApplicable;
                var total = disks.Sum(d => d.Length);
                return new TargetInspection(true, 0, IsExact: false,
                    Note: $"{disks.Count} virtual disk(s) using {FormatGb(total)}. Savings depend on how much was deleted inside them.");
            },
            Execute = ct => MeasureFreed(ct, () =>
            {
                var wsl = ProcessRunner.SystemTool("wsl.exe");
                if (File.Exists(wsl))
                    ProcessRunner.Run(wsl, ["--shutdown"], ct);

                var last = new ProcessResult(0, string.Empty);
                foreach (var disk in FindVirtualDisks())
                {
                    var script = Path.Combine(Path.GetTempPath(), $"sweepv-compact-{Guid.NewGuid():N}.txt");
                    File.WriteAllLines(script,
                    [
                        $"select vdisk file=\"{disk.FullName}\"",
                        "attach vdisk readonly",
                        "compact vdisk",
                        "detach vdisk"
                    ]);
                    try
                    {
                        last = ProcessRunner.Run(ProcessRunner.SystemTool("diskpart.exe"), ["/s", script], ct);
                    }
                    finally
                    {
                        File.Delete(script);
                    }
                }
                return last;
            })
        };

        private static IEnumerable<FileInfo> FindVirtualDisks()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(local, @"Docker\wsl\disk\docker_data.vhdx"),
                Path.Combine(local, @"Docker\wsl\data\ext4.vhdx"),
                Path.Combine(local, @"Docker\wsl\main\ext4.vhdx")
            };
            // Store-installed distros (Ubuntu, Debian, ...) and newer `wsl --install` locations.
            candidates.AddRange(SafeDirectories(Path.Combine(local, "Packages"))
                .Select(d => Path.Combine(d, @"LocalState\ext4.vhdx")));
            candidates.AddRange(SafeDirectories(Path.Combine(local, "wsl"))
                .Select(d => Path.Combine(d, "ext4.vhdx")));

            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Select(p => new FileInfo(p));
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Runs <paramref name="action"/> and reports the change in free space on the system drive.</summary>
        private static CleanupResult MeasureFreed(CancellationToken ct, Func<ProcessResult> action)
        {
            ct.ThrowIfCancellationRequested();
            var before = new DriveInfo(SystemDrive()).AvailableFreeSpace;
            var result = action();
            var freed = Math.Max(0, new DriveInfo(SystemDrive()).AvailableFreeSpace - before);
            return result.Succeeded
                ? new CleanupResult(freed, 1, 0)
                : new CleanupResult(freed, 0, 1, Message: "Windows reported an error: " + LastLine(result.Output));
        }

        internal static string SystemDrive() =>
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        internal static FileInfo? SystemDriveFile(string name)
        {
            try
            {
                // Enumeration reads the size from the directory entry, which works even for locked system files.
                return new DirectoryInfo(SystemDrive())
                    .EnumerateFiles(name, new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static long ParseLabeledSize(string output, string label)
        {
            var match = Regex.Match(output, Regex.Escape(label) + @"\s*:\s*([\d.,]+)\s*(bytes|B|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            return match.Success ? ToBytes(match.Groups[1].Value, match.Groups[2].Value) : 0;
        }

        private static long ToBytes(string number, string unit)
        {
            // Accept both "1.5" and "1,5" regardless of the OS locale.
            var normalized = number.Replace(',', '.');
            if (normalized.Count(c => c == '.') > 1)
                normalized = normalized.Replace(".", "");
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return 0;
            var multiplier = unit.ToUpperInvariant() switch
            {
                "KB" => 1024d,
                "MB" => 1024d * 1024,
                "GB" => 1024d * 1024 * 1024,
                "TB" => 1024d * 1024 * 1024 * 1024,
                _ => 1d
            };
            return (long)(value * multiplier);
        }

        private static string FormatGb(long bytes) => $"{bytes / (1024d * 1024 * 1024):0.#} GB";

        public static string LastLine(string output) =>
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";

        private static IEnumerable<string> SafeDirectories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return [];
            }
        }

        [GeneratedRegex(@"HarddiskVolumeShadowCopy\d+", RegexOptions.IgnoreCase)]
        private static partial Regex ShadowVolumeRegex();

        [GeneratedRegex(@"([\d]+(?:[.,]\d+)?)\s*(B|KB|MB|GB|TB)\b")]
        private static partial Regex SizeRegex();
    }
}
