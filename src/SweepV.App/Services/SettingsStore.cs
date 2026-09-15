using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SweepV.App.Services
{
    /// <summary>
    /// Persists user settings under %AppData%\SweepV. The API key is encrypted with
    /// DPAPI so it can only be read by the current Windows user.
    /// </summary>
    public sealed class SettingsStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SweepV", "settings.json");

        public string? LoadApiKey()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath));
                    if (!string.IsNullOrEmpty(data?.EncryptedGeminiKey))
                    {
                        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(data.EncryptedGeminiKey), null, DataProtectionScope.CurrentUser);
                        return Encoding.UTF8.GetString(bytes);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or FormatException)
            {
                // Corrupt or foreign settings: fall through to the environment variable.
            }
            return Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        }

        public void SaveApiKey(string? apiKey)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var encrypted = string.IsNullOrWhiteSpace(apiKey)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SettingsFile(encrypted)));
        }

        private sealed record SettingsFile(string? EncryptedGeminiKey);
    }
}
