using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SweepV.Core.Models;

namespace SweepV.Core.Ai
{
    public enum ChatRole { User, Model }

    public sealed record ChatMessage(ChatRole Role, string Text);

    /// <summary>
    /// Talks to the Gemini API to explain what a folder is and whether it can be deleted.
    /// Only metadata (paths, names, sizes, dates) is sent — never file contents.
    /// </summary>
    public sealed class GeminiFolderAdvisor(HttpClient httpClient, string apiKey, GeminiModel model, ThinkingEffort effort)
    {
        private const int MaxChildrenInContext = 30;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Builds the first user message describing the folder.</summary>
        public static string BuildFolderQuestion(ScanNode node)
        {
            var sb = new StringBuilder();
            sb.AppendLine("I found this item while cleaning my disk. What is it, which app or Windows component does it belong to, and can I delete it?");
            sb.AppendLine();
            sb.AppendLine($"Path: {node.FullPath}");
            sb.AppendLine($"Type: {(node.IsDirectory ? "Folder" : "File")}");
            sb.AppendLine($"Size: {FormatBytes(node.SizeInBytes)}");
            if (node.IsDirectory)
                sb.AppendLine($"Files inside: {node.FileCount:N0}");
            if (node.LastModifiedUtc != default)
                sb.AppendLine($"Last modified: {node.LastModifiedUtc:yyyy-MM-dd}");

            if (node.Children.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Largest items inside (top {Math.Min(MaxChildrenInContext, node.Children.Count)} of {node.Children.Count}):");
                foreach (var child in node.Children.Take(MaxChildrenInContext))
                    sb.AppendLine($"- {child.Name}{(child.IsDirectory ? "\\" : "")}  {FormatBytes(child.SizeInBytes)}");
            }
            return sb.ToString();
        }

        public async Task<string> AskAsync(IReadOnlyList<ChatMessage> conversation, CancellationToken cancellationToken = default)
        {
            var request = new GenerateContentRequest(
                SystemInstruction: new Content(null, [new Part(BuildSystemPrompt())]),
                Contents: conversation.Select(m => new Content(m.Role == ChatRole.User ? "user" : "model", [new Part(m.Text)])).ToList(),
                GenerationConfig: new GenerationConfig(BuildThinkingConfig()));

            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model.Id)}:generateContent")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            message.Headers.Add("x-goog-api-key", apiKey);

            using var response = await httpClient.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new GeminiException($"Gemini API error ({(int)response.StatusCode}): {ExtractError(body)}");

            var parsed = JsonSerializer.Deserialize<GenerateContentResponse>(body, JsonOptions);
            var text = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts?
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrEmpty(t));
            var answer = text is null ? string.Empty : string.Concat(text);

            return string.IsNullOrWhiteSpace(answer)
                ? throw new GeminiException("Gemini returned an empty answer.")
                : answer.Trim();
        }

        private ThinkingConfig BuildThinkingConfig()
        {
            var level = model.Efforts.Contains(effort) ? effort : model.Efforts[0];

            // Gemini 3+ takes a named level; 2.5 models only understand a token budget.
            if (!model.UsesThinkingBudget)
                return new ThinkingConfig(ThinkingLevel: level.ToString().ToLowerInvariant(), ThinkingBudget: null);

            return new ThinkingConfig(ThinkingLevel: null, ThinkingBudget: level switch
            {
                ThinkingEffort.Minimal or ThinkingEffort.Low => 1024,
                ThinkingEffort.Medium => 8192,
                _ => 24576
            });
        }

        private static string BuildSystemPrompt()
        {
            var language = CultureInfo.CurrentUICulture.EnglishName;
            return $"""
                You are the built-in assistant of SweepV, a Windows disk cleanup tool used by non-technical people.
                The user shows you a file or folder from their disk (path, size and the names of the largest items inside).
                Your job:
                1. Explain in plain words what it is and which application or Windows component created it.
                2. Give a clear verdict on its own line, exactly one of:
                   "✅ Safe to delete", "⚠️ Delete with caution", "⛔ Do not delete".
                3. Explain what happens if it is deleted (data loss, app breaks, gets recreated, etc.).
                4. Recommend the proper way to reclaim the space when plain deletion is wrong
                   (uninstalling the app, the app's own settings, Windows Settings > Storage, Disk Cleanup, etc.).
                Rules: Be honest when you are not sure and say what would help identify it. Never encourage deleting
                anything under C:\Windows, Program Files or active user profile data without a strong reason.
                Keep answers short and skimmable (bullets are fine). Reply in {language} unless the user writes in another language,
                in which case reply in the user's language.
                """;
        }

        private static string ExtractError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? body;
            }
            catch (JsonException)
            {
            }
            return body.Length > 300 ? body[..300] : body;
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            var unit = 0;
            while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
        }

        private sealed record Part(string? Text);
        private sealed record Content(string? Role, List<Part>? Parts);
        private sealed record ThinkingConfig(string? ThinkingLevel, int? ThinkingBudget);
        private sealed record GenerationConfig(ThinkingConfig ThinkingConfig);
        private sealed record GenerateContentRequest(Content SystemInstruction, List<Content> Contents, GenerationConfig GenerationConfig);
        private sealed record Candidate(Content? Content);
        private sealed record GenerateContentResponse(List<Candidate>? Candidates);
    }

    public sealed class GeminiException(string message) : Exception(message);
}
