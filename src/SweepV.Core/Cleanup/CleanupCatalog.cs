namespace SweepV.Core.Cleanup
{
    /// <summary>
    /// The built-in list of known cleanup locations.
    /// </summary>
    public static class CleanupCatalog
    {
        public static IReadOnlyList<CleanupTarget> CreateDefault()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";

            return
            [
                new CleanupTarget
                {
                    Id = "user-temp",
                    Name = "User temporary files",
                    Description = "Temporary files left behind by apps and installers (%TEMP%). Files in use are skipped.",
                    IsRecommended = true,
                    ResolveFolders = () => [Path.GetTempPath()]
                },
                new CleanupTarget
                {
                    Id = "windows-temp",
                    Name = "Windows temporary files",
                    Description = "System-wide temporary folder (C:\\Windows\\Temp).",
                    IsRecommended = true,
                    RequiresAdmin = true,
                    ResolveFolders = () => [Path.Combine(windows, "Temp")]
                },
                new CleanupTarget
                {
                    Id = "recycle-bin",
                    Name = "Recycle Bin",
                    Description = "Files you already deleted. Emptying it makes them unrecoverable.",
                    Kind = CleanupKind.RecycleBin,
                    IsRecommended = true
                },
                new CleanupTarget
                {
                    Id = "windows-update",
                    Name = "Windows Update cache",
                    Description = "Downloaded update packages that are already installed. Windows re-downloads if needed.",
                    IsRecommended = true,
                    RequiresAdmin = true,
                    ResolveFolders = () => [Path.Combine(windows, "SoftwareDistribution", "Download")]
                },
                new CleanupTarget
                {
                    Id = "delivery-optimization",
                    Name = "Delivery Optimization cache",
                    Description = "Update pieces cached to share with other PCs.",
                    IsRecommended = true,
                    RequiresAdmin = true,
                    ResolveFolders = () => [Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")]
                },
                new CleanupTarget
                {
                    Id = "error-reports",
                    Name = "Windows error reports",
                    Description = "Crash and problem reports already sent (or queued) to Microsoft.",
                    IsRecommended = true,
                    ResolveFolders = () =>
                    [
                        Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"),
                        Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"),
                        Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
                        Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue")
                    ]
                },
                new CleanupTarget
                {
                    Id = "crash-dumps",
                    Name = "Crash dumps",
                    Description = "Memory dumps created when apps crash. Only useful for debugging.",
                    IsRecommended = true,
                    ResolveFolders = () => [Path.Combine(local, "CrashDumps")]
                },
                new CleanupTarget
                {
                    Id = "thumbnail-cache",
                    Name = "Thumbnail cache",
                    Description = "Explorer picture previews. Rebuilt automatically when folders are opened.",
                    IsRecommended = true,
                    ResolveFolders = () => [Path.Combine(local, @"Microsoft\Windows\Explorer")],
                    FilePattern = "thumbcache_*.db"
                },
                new CleanupTarget
                {
                    Id = "shader-cache",
                    Name = "GPU shader caches",
                    Description = "DirectX / NVIDIA / AMD shader caches. Games may stutter briefly while rebuilding.",
                    IsRecommended = true,
                    ResolveFolders = () =>
                    [
                        Path.Combine(local, "D3DSCache"),
                        Path.Combine(local, @"NVIDIA\DXCache"),
                        Path.Combine(local, @"NVIDIA\GLCache"),
                        Path.Combine(local, @"AMD\DxCache")
                    ]
                },
                new CleanupTarget
                {
                    Id = "browser-cache",
                    Name = "Browser caches",
                    Description = "Cached web content for Chrome, Edge, Brave and Firefox. Logins and history are kept. Close browsers first.",
                    IsRecommended = true,
                    ResolveFolders = () => ResolveBrowserCaches(local)
                },
                new CleanupTarget
                {
                    Id = "downloads",
                    Name = "Downloads folder",
                    Description = "Everything in your Downloads folder. May contain personal files — review before cleaning.",
                    Risk = CleanupRisk.Caution,
                    ResolveFolders = () => [Path.Combine(profile, "Downloads")]
                },
                new CleanupTarget
                {
                    Id = "windows-old",
                    Name = "Previous Windows installation (Windows.old)",
                    Description = "Left after a Windows upgrade. Removing it means you can no longer roll back to the previous version.",
                    Risk = CleanupRisk.Caution,
                    RequiresAdmin = true,
                    ResolveFolders = () => [Path.Combine(systemDrive, "Windows.old")]
                }
            ];
        }

        private static IEnumerable<string> ResolveBrowserCaches(string local)
        {
            string[] chromiumUserData =
            [
                Path.Combine(local, @"Google\Chrome\User Data"),
                Path.Combine(local, @"Microsoft\Edge\User Data"),
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")
            ];

            foreach (var userData in chromiumUserData.Where(Directory.Exists))
            {
                foreach (var profileDir in SafeDirectories(userData))
                {
                    yield return Path.Combine(profileDir, "Cache");
                    yield return Path.Combine(profileDir, "Code Cache");
                    yield return Path.Combine(profileDir, "GPUCache");
                }
            }

            foreach (var profileDir in SafeDirectories(Path.Combine(local, @"Mozilla\Firefox\Profiles")))
                yield return Path.Combine(profileDir, "cache2");
        }

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
    }
}
