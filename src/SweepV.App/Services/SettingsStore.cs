using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SweepV.Core.Ai;

namespace SweepV.App.Services
{
    public sealed record AiSettings(string? ApiKey, string ModelId, ThinkingEffort Effort);

    /// <summary>
    /// Persists user settings under %AppData%\SweepV. The API key is encrypted with
    /// DPAPI so it can only be read by the current Windows user.
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SweepV", "settings.json");

        public AiSettings Load()
        {
            SettingsFile? data = null;
            string? apiKey = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    data = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath));
                    if (!string.IsNullOrEmpty(data?.EncryptedGeminiKey))
                    {
                        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(data.EncryptedGeminiKey), null, DataProtectionScope.CurrentUser);
                        apiKey = Encoding.UTF8.GetString(bytes);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException)
            {
                // Corrupt or foreign settings: fall back to defaults and the environment variable.
            }

            return new AiSettings(
                apiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
                GeminiModels.Find(data?.GeminiModel).Id,
                data?.ThinkingEffort ?? GeminiModels.DefaultEffort);
        }

        public void Save(AiSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var encrypted = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.ApiKey.Trim()), null, DataProtectionScope.CurrentUser));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SettingsFile(encrypted, settings.ModelId, settings.Effort)));
        }

        private sealed record SettingsFile(string? EncryptedGeminiKey, string? GeminiModel = null, ThinkingEffort? ThinkingEffort = null);
    }
}
