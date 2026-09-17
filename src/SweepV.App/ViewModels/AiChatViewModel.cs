using System.Collections.ObjectModel;
using System.IO;
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
            var saved = settings.Load();
            _apiKey = saved.ApiKey ?? string.Empty;
            _isKeyEditorOpen = string.IsNullOrEmpty(_apiKey);
            Messages.CollectionChanged += (_, _) => ClearChatCommand.NotifyCanExecuteChanged();
            _selectedModel = GeminiModels.Find(saved.ModelId);
            _selectedEffort = _selectedModel.Efforts.Contains(saved.Effort) ? saved.Effort : _selectedModel.Efforts[0];
        }

        public IReadOnlyList<GeminiModel> Models => GeminiModels.All;

        /// <summary>Efforts supported by the selected model.</summary>
        public IReadOnlyList<ThinkingEffort> Efforts => SelectedModel.Efforts;

        [ObservableProperty]
        private GeminiModel _selectedModel;

        [ObservableProperty]
        private ThinkingEffort _selectedEffort;

        partial void OnSelectedModelChanged(GeminiModel value)
        {
            OnPropertyChanged(nameof(Efforts));
            if (!value.Efforts.Contains(SelectedEffort))
                SelectedEffort = value.Efforts.Contains(ThinkingEffort.Low) ? ThinkingEffort.Low : value.Efforts[0];
            PersistSettings();
        }

        partial void OnSelectedEffortChanged(ThinkingEffort value) => PersistSettings();

        private void PersistSettings()
        {
            try
            {
                _settings.Save(new AiSettings(ApiKey, SelectedModel.Id, SelectedEffort));
            }
            catch (IOException)
            {
                // Settings are a convenience; the current session keeps working.
            }
        }

        public ObservableCollection<ChatBubble> Messages { get; } = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(ClearChatCommand))]
        private string? _subjectPath;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private string _draft = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private bool _isThinking;

        // Never bound to the UI: the view only ever sees MaskedApiKey.
        private string _apiKey;
        private string ApiKey
        {
            get => _apiKey;
            set
            {
                _apiKey = value;
                OnPropertyChanged(nameof(HasApiKey));
                OnPropertyChanged(nameof(MaskedApiKey));
                RemoveApiKeyCommand.NotifyCanExecuteChanged();
            }
        }

        [ObservableProperty]
        private bool _isKeyEditorOpen;

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

        /// <summary>First 4 and last 4 characters only, e.g. "AIza••••••••wXyz".</summary>
        public string MaskedApiKey => ApiKey.Trim() switch
        {
            { Length: 0 } => "No key saved",
            { Length: <= 10 } k => new string('•', k.Length),
            var k => $"{k[..4]}••••••••{k[^4..]}"
        };

        /// <summary>Called by the view with the contents of the password box, which is then cleared.</summary>
        public void SaveApiKey(string newKey)
        {
            if (string.IsNullOrWhiteSpace(newKey))
                return;
            ApiKey = newKey.Trim();
            PersistSettings();
            IsKeyEditorOpen = false;
        }

        [RelayCommand(CanExecute = nameof(HasApiKey))]
        private void RemoveApiKey()
        {
            if (!Dialogs.Confirm("Remove the saved Gemini API key from this PC?", "Remove API key"))
                return;
            ApiKey = string.Empty;
            PersistSettings();
            IsKeyEditorOpen = true;
        }

        [RelayCommand]
        private void ToggleKeyEditor() => IsKeyEditorOpen = !IsKeyEditorOpen;

        private bool CanClear() => Messages.Count > 0 || SubjectPath is not null;

        /// <summary>Drops the conversation (and any pending answer) so the next question starts fresh.</summary>
        [RelayCommand(CanExecute = nameof(CanClear))]
        private void ClearChat()
        {
            _cts?.Cancel();
            _history.Clear();
            Messages.Clear();
            SubjectPath = null;
            Draft = string.Empty;
            IsThinking = false;
        }

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
                var advisor = new GeminiFolderAdvisor(Http, ApiKey.Trim(), SelectedModel, SelectedEffort);
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
