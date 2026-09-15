using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SweepV.App.Services;
using SweepV.Core.Ai;
using SweepV.Core.Models;

namespace SweepV.App.ViewModels
{
    public sealed record ChatBubble(ChatRole Role, string Text)
    {
        public bool IsUser => Role == ChatRole.User;
    }

    /// <summary>
    /// A conversation with Gemini about a single file or folder.
    /// </summary>
    public partial class AiChatViewModel : ObservableObject
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly SettingsStore _settings;
        private readonly List<ChatMessage> _history = [];
        private CancellationTokenSource? _cts;

        public AiChatViewModel(SettingsStore settings)
        {
            _settings = settings;
            _apiKey = settings.LoadApiKey() ?? string.Empty;
            _isKeyEditorOpen = string.IsNullOrEmpty(_apiKey);
        }

        public ObservableCollection<ChatBubble> Messages { get; } = [];

        [ObservableProperty]
        private string? _subjectPath;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private string _draft = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private bool _isThinking;

        [ObservableProperty]
        private string _apiKey;

        [ObservableProperty]
        private bool _isKeyEditorOpen;

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

        partial void OnApiKeyChanged(string value) => OnPropertyChanged(nameof(HasApiKey));

        [RelayCommand]
        private void SaveApiKey()
        {
            _settings.SaveApiKey(ApiKey);
            IsKeyEditorOpen = !HasApiKey;
        }

        [RelayCommand]
        private void ToggleKeyEditor() => IsKeyEditorOpen = !IsKeyEditorOpen;

        /// <summary>Starts a new conversation about <paramref name="node"/>.</summary>
        public async Task StartAsync(ScanNode node)
        {
            _cts?.Cancel();
            _history.Clear();
            Messages.Clear();
            SubjectPath = node.FullPath;

            if (!HasApiKey)
            {
                IsKeyEditorOpen = true;
                Messages.Add(new ChatBubble(ChatRole.Model, "Add your Gemini API key above to ask about this folder. You can get one free at aistudio.google.com."));
                return;
            }

            await SendInternalAsync(GeminiFolderAdvisor.BuildFolderQuestion(node),
                displayText: $"What is \"{node.Name}\" and can I delete it?");
        }

        private bool CanSend() => !IsThinking && SubjectPath is not null && !string.IsNullOrWhiteSpace(Draft);

        [RelayCommand(CanExecute = nameof(CanSend))]
        private async Task SendAsync()
        {
            var text = Draft.Trim();
            Draft = string.Empty;
            await SendInternalAsync(text, text);
        }

        private async Task SendInternalAsync(string text, string displayText)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _history.Add(new ChatMessage(ChatRole.User, text));
            Messages.Add(new ChatBubble(ChatRole.User, displayText));
            IsThinking = true;
            try
            {
                var advisor = new GeminiFolderAdvisor(Http, ApiKey.Trim());
                var answer = await advisor.AskAsync(_history, token);
                if (token.IsCancellationRequested)
                    return;
                _history.Add(new ChatMessage(ChatRole.Model, answer));
                Messages.Add(new ChatBubble(ChatRole.Model, answer));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A new conversation replaced this one.
            }
            catch (Exception ex) when (ex is GeminiException or HttpRequestException or TaskCanceledException)
            {
                // Drop the unanswered question so the conversation stays valid for retries.
                _history.RemoveAt(_history.Count - 1);
                Messages.Add(new ChatBubble(ChatRole.Model, "⚠️ " + ex.Message));
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    IsThinking = false;
            }
        }
    }
}
