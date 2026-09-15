namespace SweepV.Core.Ai
{
    public enum ThinkingEffort { Minimal, Low, Medium, High }

    /// <summary>
    /// A text chat model usable by the folder advisor, with the thinking efforts it accepts.
    /// </summary>
    public sealed record GeminiModel(string Id, string DisplayName, IReadOnlyList<ThinkingEffort> Efforts, bool UsesThinkingBudget = false)
    {
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Text-chat Gemini models from https://ai.google.dev/gemini-api/docs/models (checked 2026-09).
    /// Live, TTS and translate models are left out because they don't fit a chat advisor.
    /// </summary>
    public static class GeminiModels
    {
        private static readonly ThinkingEffort[] LowToHigh = [ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High];
        private static readonly ThinkingEffort[] MinimalToHigh = [ThinkingEffort.Minimal, .. LowToHigh];

        public static readonly IReadOnlyList<GeminiModel> All =
        [
            new("gemini-3.8-flash", "Gemini 3.8 Flash", LowToHigh),
            new("gemini-3.7-flash", "Gemini 3.7 Flash", LowToHigh),
            new("gemini-3.6-flash", "Gemini 3.6 Flash", LowToHigh),
            new("gemini-3.5-flash", "Gemini 3.5 Flash", MinimalToHigh),
            new("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite", MinimalToHigh),
            new("gemini-3.1-pro-preview", "Gemini 3.1 Pro (preview)", LowToHigh),
            new("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite", MinimalToHigh),
            new("gemini-3-flash-preview", "Gemini 3 Flash (preview)", MinimalToHigh),
            new("gemini-2.5-pro", "Gemini 2.5 Pro", LowToHigh, UsesThinkingBudget: true),
            new("gemini-2.5-flash", "Gemini 2.5 Flash", LowToHigh, UsesThinkingBudget: true),
            new("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite", LowToHigh, UsesThinkingBudget: true),
        ];

        public const string DefaultModelId = "gemini-3.7-flash";
        public const ThinkingEffort DefaultEffort = ThinkingEffort.Low;

        public static GeminiModel Find(string? id) =>
            All.FirstOrDefault(m => m.Id == id) ?? All.First(m => m.Id == DefaultModelId);
    }
}
