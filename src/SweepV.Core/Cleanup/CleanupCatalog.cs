using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SweepV.Core.Cleanup
{
    /// <summary>
    /// The list of cleanup locations. Folder-based targets come from <c>cleanup-targets.json</c>
    /// (community-editable, embedded into the app); command-based ones from <see cref="SystemActions"/>.
    /// </summary>
    public static class CleanupCatalog
    {
        private const string ResourceName = "SweepV.Core.Cleanup.cleanup-targets.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        private static readonly Dictionary<string, string> CategoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = CleanupCategories.Windows,
            ["upgradeLeftovers"] = CleanupCategories.UpgradeLeftovers,
            ["browsersAndApps"] = CleanupCategories.BrowsersAndApps,
            ["gaming"] = CleanupCategories.Gaming,
            ["developer"] = CleanupCategories.Developer,
            ["personal"] = CleanupCategories.Personal
        };

        public static IReadOnlyList<CleanupTarget> CreateDefault()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
            var file = Load(stream);

            // Invalid entries are skipped rather than crashing the app; the unit tests keep the shipped file clean.
            return
            [
                .. file.Targets.Where(d => Validate(d).Count == 0).Select(ToTarget),
                .. SystemActions.Create()
            ];
        }

        public static CatalogFile Load(Stream json) =>
            JsonSerializer.Deserialize<CatalogFile>(json, JsonOptions) ?? new CatalogFile([]);

        /// <summary>Returns the problems with a definition; empty when it's valid.</summary>
        public static List<string> Validate(TargetDefinition definition)
        {
            var errors = new List<string>();
            var label = string.IsNullOrWhiteSpace(definition.Id) ? "(no id)" : definition.Id;

            if (string.IsNullOrWhiteSpace(definition.Id) || !definition.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                errors.Add($"{label}: id must be lowercase-kebab-case.");
            if (string.IsNullOrWhiteSpace(definition.Name))
                errors.Add($"{label}: name is required.");
            if (string.IsNullOrWhiteSpace(definition.Description))
                errors.Add($"{label}: description is required.");
            if (!CategoryNames.ContainsKey(definition.Category ?? string.Empty))
                errors.Add($"{label}: unknown category '{definition.Category}'. Use one of: {string.Join(", ", CategoryNames.Keys)}.");

            if (definition.Kind == DefinitionKind.RecycleBin)
                return errors;

            if (definition.Paths is not { Count: > 0 })
                errors.Add($"{label}: at least one path is required.");

            foreach (var path in definition.Paths ?? [])
            {
                var normalized = path.Replace('/', '\\').TrimEnd('\\');
                var segments = normalized.Split('\\');
                if (!segments[0].StartsWith('%') || !segments[0].EndsWith('%') || !Tokens.ContainsKey(segments[0]))
                    errors.Add($"{label}: '{path}' must start with a known token ({string.Join(", ", Tokens.Keys)}).");
                if (segments.Any(s => s is ".." or "."))
                    errors.Add($"{label}: '{path}' must not contain '.' or '..'.");
                if (segments.Skip(1).Any(s => s.Contains('%')))
                    errors.Add($"{label}: '{path}' may only use a token at the start.");
                // Cleaning a whole root (e.g. all of %USERPROFILE%) is never right; a file pattern narrows it enough.
                if (segments.Length < 2 && definition.FilePattern is null && segments[0] != "%TEMP%")
                    errors.Add($"{label}: '{path}' points at a whole root folder; add a sub-folder.");
                if (segments.Length >= 2 && segments[1] == "*" && segments.Length == 2)
                    errors.Add($"{label}: '{path}' would clean every folder in a root.");
            }
            return errors;
        }

        private static CleanupTarget ToTarget(TargetDefinition d) => new()
        {
            Id = d.Id,
            Name = d.Name,
            Description = d.Description,
            Category = CategoryNames[d.Category!],
            Kind = d.Kind == DefinitionKind.RecycleBin ? CleanupKind.RecycleBin : CleanupKind.FolderContents,
            Risk = d.Risk,
            IsRecommended = d.Recommended,
            RequiresAdmin = d.RequiresAdmin,
            RemoveFolderWithOwnership = d.RemoveFolder,
            FilePattern = d.FilePattern,
            ResolveFolders = () => (d.Paths ?? []).SelectMany(ExpandPath)
        };

        // ------------------------------------------------------------ path expansion

        private static readonly Dictionary<string, Func<string?>> Tokens = new(StringComparer.OrdinalIgnoreCase)
        {
            ["%WINDIR%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["%SYSTEMDRIVE%"] = () => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            ["%TEMP%"] = Path.GetTempPath,
            ["%LOCALAPPDATA%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["%APPDATA%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["%PROGRAMDATA%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["%USERPROFILE%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ["%PROGRAMFILES%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["%PROGRAMFILES(X86)%"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["%STEAM%"] = ResolveSteam
        };

        /// <summary>Expands the leading token and any <c>*</c> wildcards in folder names.</summary>
        public static IEnumerable<string> ExpandPath(string path)
        {
            var segments = path.Replace('/', '\\').TrimEnd('\\').Split('\\');
            if (!Tokens.TryGetValue(segments[0], out var resolve) || resolve() is not { Length: > 0 } root)
                return [];

            IEnumerable<string> current = [root];
            foreach (var segment in segments.Skip(1))
            {
                current = segment.Contains('*') || segment.Contains('?')
                    ? current.SelectMany(dir => SafeDirectories(dir, segment))
                    : current.Select(dir => Path.Combine(dir, segment));
            }
            return current;
        }

        private static string? ResolveSteam()
        {
            if (OperatingSystem.IsWindows() &&
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string registered &&
                Directory.Exists(registered))
                return Path.GetFullPath(registered);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        }

        private static IEnumerable<string> SafeDirectories(string path, string pattern)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path, pattern) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return [];
            }
        }
    }

    public enum DefinitionKind { FolderContents, RecycleBin }

    public sealed record CatalogFile(List<TargetDefinition> Targets);

    /// <summary>One entry of cleanup-targets.json.</summary>
    public sealed record TargetDefinition
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? Category { get; init; }
        public DefinitionKind Kind { get; init; } = DefinitionKind.FolderContents;
        public CleanupRisk Risk { get; init; } = CleanupRisk.Safe;
        public bool Recommended { get; init; }
        public bool RequiresAdmin { get; init; }
        public bool RemoveFolder { get; init; }
        public string? FilePattern { get; init; }
        public List<string>? Paths { get; init; }
    }
}
