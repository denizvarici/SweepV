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
            ReservedStorage(),
            Hibernation(),
            RestorePoints(),
            VirtualDiskCompaction()
        ];

        // ------------------------------------------------------------ WinSxS

        private static CleanupTarget ComponentStoreCleanup() => new()
        {
            Id = "component-store",
            Name = "Clean up Windows component store (WinSxS)",
            Description = "Removes superseded versions of Windows components left by updates, using DISM. Safe, but can take 10+ minutes.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            IsRecommended = true,
            RequiresAdmin = true,
            Inspect = ct =>
            {
                if (!Elevation.IsElevated)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Restart as administrator to see how much can be freed.");

                // /English keeps the output parseable on localized Windows.
                var result = ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"),
                    ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore", "/English"], ct);
                if (!result.Succeeded)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Windows could not analyze the component store right now.");

                var reclaimable = ParseLabeledSize(result.Output, "Backups and Disabled Features") +
                                  ParseLabeledSize(result.Output, "Cache and Temporary Data");
                var recommended = Regex.IsMatch(result.Output, @"Component Store Cleanup Recommended\s*:\s*Yes", RegexOptions.IgnoreCase);
                return new TargetInspection(true, reclaimable, IsExact: false,
                    Note: recommended ? "Windows recommends running this cleanup." : "Windows says a cleanup isn't needed right now.");
            },
            Execute = ct => MeasureFreed(ct, () =>
                ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"),
                    ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/English"], ct))
        };

        // ------------------------------------------------------------ Reserved storage

        private const long TypicalReservedStorage = 7L * 1024 * 1024 * 1024;

        private static CleanupTarget ReservedStorage() => new()
        {
            Id = "reserved-storage",
            Name = "Turn off Reserved Storage",
            Description = "Windows keeps about 7 GB aside so updates always have room. Turning it off gives that space back, but large updates may fail when the disk is nearly full.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            Risk = CleanupRisk.Caution,
            RequiresAdmin = true,
            Inspect = ct =>
            {
                if (!Elevation.IsElevated)
                    return new TargetInspection(true, TypicalReservedStorage, IsExact: false, Note: "Restart as administrator to check whether it is enabled.");

                var result = ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"), ["/Online", "/Get-ReservedStorageState", "/English"], ct);
                return result.Succeeded && result.Output.Contains("Reserved storage is enabled", StringComparison.OrdinalIgnoreCase)
                    ? new TargetInspection(true, TypicalReservedStorage, IsExact: false)
                    : TargetInspection.NotApplicable;
            },
            Execute = ct => MeasureFreed(ct, () =>
                ProcessRunner.Run(ProcessRunner.SystemTool("Dism.exe"), ["/Online", "/Set-ReservedStorageState", "/State:Disabled", "/English"], ct))
        };

        // ------------------------------------------------------------ hiberfil.sys

        private static CleanupTarget Hibernation() => new()
        {
            Id = "hibernation",
            Name = "Turn off hibernation (hiberfil.sys)",
            Description = "Deletes the hibernation file, which is often 40% of your RAM. You lose Hibernate and Fast Startup; laptops that hibernate on low battery will shut down instead.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            Risk = CleanupRisk.Caution,
            RequiresAdmin = true,
            Inspect = _ =>
            {
                var file = SystemDriveFile("hiberfil.sys");
                return file is null ? TargetInspection.NotApplicable : new TargetInspection(true, file.Length);
            },
            Execute = ct => MeasureFreed(ct, () =>
                ProcessRunner.Run(ProcessRunner.SystemTool("powercfg.exe"), ["/hibernate", "off"], ct))
        };

        // ------------------------------------------------------------ restore points

        private static CleanupTarget RestorePoints() => new()
        {
            Id = "restore-points",
            Name = "Delete older restore points",
            Description = "Removes all System Restore points except the most recent one. You can no longer roll back to those older points.",
            Category = CleanupCategories.SystemActions,
            Kind = CleanupKind.Command,
            Risk = CleanupRisk.Caution,
            RequiresAdmin = true,
            Inspect = ct =>
            {
                if (!Elevation.IsElevated)
                    return new TargetInspection(true, 0, IsExact: false, Note: "Restart as administrator to check restore points.");

                var count = CountShadowCopies(ct);
                if (count <= 1)
                    return TargetInspection.NotApplicable;

                // Rough estimate: everything but the newest point's share of used shadow storage.
                var used = UsedShadowStorage(ct);
                return new TargetInspection(true, used * (count - 1) / count, IsExact: false,
                    Note: $"{count} restore points found; the newest one is kept.");
            },
            Execute = ct => MeasureFreed(ct, () =>
            {
                var drive = SystemDrive().TrimEnd('\\');
                var last = new ProcessResult(0, string.Empty);
                // vssadmin can only delete the oldest one at a time.
                for (var count = CountShadowCopies(ct); count > 1; count--)
                {
                    last = ProcessRunner.Run(ProcessRunner.SystemTool("vssadmin.exe"), ["delete", "shadows", $"/for={drive}", "/oldest", "/quiet"], ct);
                    if (!last.Succeeded)
                        break;
                }
                return last;
            })
        };

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

        private static string SystemDrive() =>
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        private static FileInfo? SystemDriveFile(string name)
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

        private static string LastLine(string output) =>
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
