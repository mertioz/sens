using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Sens;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(40)
    };
    private readonly CodexBridge codex = new();
    private readonly Translator translator;
    private Preferences preferences = Preferences.Load();
    private CancellationTokenSource? request, modelRequest;
    private bool populating, resumeTranslation, placeQueued, replying;
    private string? selectedText, sourceLanguage;
    private string codexPathDraft = "";
    private (Win32.PixelPoint Click, Rect[] Rects)? anchor;
    private readonly DispatcherTimer bringToFront = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer copyNotice = new() { Interval = TimeSpan.FromSeconds(1.2) };

    public MainWindow()
    {
        translator = new(Http, codex);
        InitializeComponent();
        StylePromptBox.MaxLength = Preferences.StylePromptLimit;
        LanguageBox.ItemsSource = new[]
        {
            "Français", "Anglais", "Anglais (États-Unis)", "Anglais (Royaume-Uni)", "Allemand", "Arabe",
            "Bengali", "Chinois simplifié", "Chinois traditionnel", "Coréen", "Danois", "Espagnol",
            "Grec", "Hébreu", "Hindi", "Indonésien", "Italien", "Japonais", "Néerlandais", "Norvégien",
            "Persan", "Polonais", "Portugais (Brésil)", "Portugais (Portugal)", "Roumain", "Russe",
            "Suédois", "Swahili", "Tamoul", "Thaï", "Tchèque", "Turc", "Ukrainien", "Vietnamien"
        };
        bringToFront.Tick += (_, _) => { bringToFront.Stop(); Topmost = false; };
        copyNotice.Tick += (_, _) =>
        {
            copyNotice.Stop();
            if (Status.Text == "Copié") Message(Status, "");
            if (ReplyStatus.Text == "Copié") Message(ReplyStatus, "");
        };
        RefreshHeader();
        RefreshActions();
        if (preferences.Language.Length == 0) ShowSettings();
    }

    internal void SetIntegrationStatus(bool shortcut, bool context) =>
        ShortcutNote.Text = (shortcut, context) switch
        {
            (true, true) => "Ouvrir : Ctrl + Alt + T",
            (true, false) => "Clic droit indisponible · Ctrl + Alt + T",
            (false, true) => "Ouvrir : icône près de l’heure",
            _ => "Ouvrir : icône près de l’heure · clic droit indisponible"
        };

    internal void Open()
    {
        if (selectedText is not null) ClearSelection();
        if (!IsVisible) Show();
        Present();
        if (TranslateView.IsVisible) SourceText.Focus();
        else if (preferences.Language.Length == 0) LanguageBox.Focus();
    }

    internal async void OpenSelection(TextSelection selection, Win32.PixelPoint point)
    {
        CancelRequest();
        selectedText = selection.Text;
        anchor = (point, selection.Rects);
        ResetResult();
        resumeTranslation = !Configured();
        if (resumeTranslation)
        {
            ShowSettings();
            Message(SettingsError, preferences.Language.Length == 0
                ? "Choisis la langue dans laquelle lire ce texte." : "Ajoute une clé API ou choisis Codex.");
        }
        else
        {
            ShowTranslation();
            Message(Status, "Traduction…");
        }
        if (!IsVisible) Show();
        UpdateLayout();
        PlaceNearSelection();
        Present();
        if (!resumeTranslation) await Translate();
    }

    private void Present()
    {
        if (Activate()) return;
        Topmost = true;
        bringToFront.Stop();
        bringToFront.Start();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (anchor is null || placeQueued) return;
        placeQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            placeQueued = false;
            PlaceNearSelection();
        }, DispatcherPriority.Render);
    }

    private void PlaceNearSelection()
    {
        if (anchor is { } place && IsVisible) Win32.PlaceTranslation(this, place.Click, place.Rects);
    }

    private async void ToggleSettings(object sender, RoutedEventArgs e)
    {
        if (!SettingsView.IsVisible)
        {
            if (request is not null)
            {
                resumeTranslation |= !replying;
                CancelRequest();
                Message(Status, "");
                Message(ReplyStatus, "");
            }
            ShowSettings();
            return;
        }

        ShowTranslation();
        if (resumeTranslation && Configured()) await Translate();
        else if (selectedText is not null && ResultText.Text.Length == 0)
        {
            Message(Status, "Configure la traduction dans les réglages.");
            RetryButton.Visibility = Visibility.Visible;
        }
    }

    private void ShowSettings()
    {
        populating = true;
        LanguageBox.Text = preferences.Language;
        StylePromptBox.Text = preferences.StylePrompt;
        codexPathDraft = preferences.CodexPath;
        ProviderBox.SelectedIndex = (int)preferences.Provider;
        KeyBox.Clear();
        populating = false;
        TranslateView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SettingsButton.ToolTip = "Revenir à la traduction";
        HeaderCaption.Text = "Réglages";
        Message(SettingsError, "");
        RefreshProvider(preferences.Model);
    }

    private void ShowTranslation()
    {
        modelRequest?.Cancel();
        KeyBox.Clear();
        SettingsView.Visibility = Visibility.Collapsed;
        TranslateView.Visibility = Visibility.Visible;
        SettingsButton.ToolTip = "Réglages";
        RefreshHeader();
        RefreshActions();
        if (selectedText is null && IsVisible) SourceText.Focus();
    }

    private Provider EditingProvider => ProviderBox.SelectedIndex >= 0
        ? (Provider)ProviderBox.SelectedIndex : Provider.OpenAI;

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeyBox is null || populating) return;
        KeyBox.Clear();
        Message(SettingsError, "");
        RefreshProvider(EditingProvider == preferences.Provider ? preferences.Model : "");
    }

    private void SetModels(IReadOnlyList<ModelChoice> models, string preferred)
    {
        ModelBox.ItemsSource = models;
        ModelBox.SelectedItem = models.FirstOrDefault(m => m.Id == preferred) ?? models[0];
    }

    private void RefreshProvider(string preferred)
    {
        modelRequest?.Cancel();
        var provider = EditingProvider;
        bool isCodex = provider == Provider.Codex;
        ApiSettings.Visibility = isCodex ? Visibility.Collapsed : Visibility.Visible;
        CodexSettings.Visibility = isCodex ? Visibility.Visible : Visibility.Collapsed;
        if (isCodex)
        {
            SetModels(preferred.Length > 0 ? [ModelChoice.Automatic, new(preferred, preferred)] : [ModelChoice.Automatic], preferred);
            ForgetButton.Visibility = Visibility.Collapsed;
            CodexPathButton.ToolTip = codexPathDraft.Length == 0 ? "Choisir une installation personnalisée" : codexPathDraft;
            CodexAutoButton.Visibility = codexPathDraft.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            CodexNote.Text = "Connexion à Codex…";
            _ = LoadCodexModels();
            return;
        }

        SetModels(ModelChoice.For(provider), preferred);
        KeyLink.NavigateUri = new Uri(provider switch
        {
            Provider.OpenAI => "https://platform.openai.com/api-keys",
            Provider.Claude => "https://platform.claude.com/settings/keys",
            _ => "https://platform.deepseek.com/api_keys"
        });
        bool hasKey = !string.IsNullOrWhiteSpace(Credentials.Get(provider));
        ForgetButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        KeyNote.Text = hasKey ? "Clé disponible. Laisse le champ vide pour la garder." : provider switch
        {
            Provider.OpenAI => "La facturation API est distincte de ChatGPT.",
            Provider.Claude => "Utilise une clé de la console Claude.",
            _ => "Utilise une clé DeepSeek."
        };
    }

    private void ChooseCodex(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choisir une installation Codex",
            Filter = "Exécutable Codex (codex.exe)|codex.exe",
            FileName = "codex.exe",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        if (!CodexLocator.IsExecutablePath(picker.FileName))
        {
            Message(SettingsError, "Choisis le fichier codex.exe de ton installation.");
            return;
        }
        codexPathDraft = picker.FileName;
        Message(SettingsError, "");
        RefreshProvider((ModelBox.SelectedItem as ModelChoice)?.Id ?? "");
    }

    private void UseAutomaticCodex(object sender, RoutedEventArgs e)
    {
        codexPathDraft = "";
        Message(SettingsError, "");
        RefreshProvider((ModelBox.SelectedItem as ModelChoice)?.Id ?? "");
    }

    private async Task LoadCodexModels()
    {
        using var current = new CancellationTokenSource(TimeSpan.FromSeconds(14));
        modelRequest = current;
        try
        {
            var models = await codex.ReadModelsAsync(current.Token, codexPathDraft);
            if (modelRequest != current || !SettingsView.IsVisible || EditingProvider != Provider.Codex) return;
            var chosen = (ModelBox.SelectedItem as ModelChoice)?.Id ?? "";
            SetModels([ModelChoice.Automatic, .. models], chosen);
            CodexNote.Text = "Compte ChatGPT connecté · utilise ton quota Codex.";
        }
        catch (Exception error) when (error is TranslationException or OperationCanceledException or
                                      IOException or UnauthorizedAccessException or JsonException or
                                      KeyNotFoundException or InvalidOperationException)
        {
            if (modelRequest != current || EditingProvider != Provider.Codex || !SettingsView.IsVisible) return;
            CodexNote.Text = error is TranslationException ? error.Message
                : "Connexion Codex indisponible. Vérifie Codex CLI et réessaie.";
        }
        finally { if (modelRequest == current) modelRequest = null; }
    }

    private void OpenKeyPage(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { Message(SettingsError, "Impossible d’ouvrir le navigateur."); }
        e.Handled = true;
    }

    private void ForgetKey(object sender, RoutedEventArgs e)
    {
        if (EditingProvider == Provider.Codex) return;
        try
        {
            var model = (ModelBox.SelectedItem as ModelChoice)?.Id ?? "";
            Credentials.Remove(EditingProvider);
            RefreshProvider(model);
        }
        catch (Win32Exception) { Message(SettingsError, "Windows n’a pas pu retirer la clé."); }
    }

    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        var language = LanguageBox.Text.Trim();
        if (!TranslationFormat.ValidLanguage(language))
        {
            Message(SettingsError, "Choisis une langue ou saisis son nom.");
            LanguageBox.Focus();
            return;
        }
        var key = KeyBox.Password.Trim();
        if (EditingProvider != Provider.Codex && key.Any(c => c < 33 || c > 126))
        {
            Message(SettingsError, "Vérifie que la clé API a été copiée correctement.");
            KeyBox.Focus();
            return;
        }

        var next = new Preferences(language, EditingProvider, (ModelBox.SelectedItem as ModelChoice)?.Id ?? "",
            Preferences.NormalizeStyle(StylePromptBox.Text), codexPathDraft);
        try
        {
            if (next.Provider != Provider.Codex && key.Length != 0) Credentials.Set(next.Provider, key);
            next.Save();
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Message(SettingsError, "Windows n’a pas pu enregistrer les réglages.");
            return;
        }

        if (next != preferences)
        {
            ResetResult();
            resumeTranslation |= selectedText is not null;
        }
        preferences = next;
        ShowTranslation();
        if (resumeTranslation && Configured()) await Translate();
        else if (!Configured() && (selectedText is not null || !string.IsNullOrWhiteSpace(SourceText.Text)))
        {
            Message(Status, "Ajoute une clé API ou choisis Codex.");
            if (selectedText is not null) RetryButton.Visibility = Visibility.Visible;
        }
    }

    private bool Configured() => preferences.Language.Length != 0 &&
        (preferences.Provider == Provider.Codex || !string.IsNullOrWhiteSpace(Credentials.Get(preferences.Provider)));

    private void RefreshHeader()
    {
        if (SettingsView.IsVisible)
        {
            HeaderCaption.Text = "Réglages";
            return;
        }

        ManualInput.Visibility = selectedText is null ? Visibility.Visible : Visibility.Collapsed;
        var target = preferences.Language.Length > 0 ? preferences.Language : "Traduction";
        HeaderCaption.Text = selectedText is not null && sourceLanguage is { } original
            ? $"{original} → {target}" : target;
        ReplyPlaceholder.Text = "Écris ta réponse…";
    }

    private static void Message(TextBlock block, string message)
    {
        block.Text = message;
        block.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshActions()
    {
        if (TranslateButton is null || ReplyButton is null) return;
        TranslateButton.IsEnabled = !string.IsNullOrWhiteSpace(SourceText.Text) && request is null;
        ReplyButton.IsEnabled = !string.IsNullOrWhiteSpace(ReplyText.Text) && request is null;
        RetryButton.IsEnabled = request is null;
    }

    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        if (ResultCard is null || selectedText is not null) return;
        CancelRequest();
        resumeTranslation = false;
        ResetResult();
    }

    private void ReplyChanged(object sender, TextChangedEventArgs e)
    {
        if (ReplyCard is null) return;
        if (replying) CancelRequest();
        ReplyResult.Clear();
        ReplyCard.Visibility = Visibility.Collapsed;
        Message(ReplyStatus, "");
        RefreshActions();
    }

    private void ResetResult()
    {
        sourceLanguage = null;
        ResultText.Clear();
        ResultCard.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ReplyPanel.Visibility = Visibility.Collapsed;
        ReplyText.Clear();
        ReplyLanguage.Clear();
        ReplyResult.Clear();
        ReplyCard.Visibility = Visibility.Collapsed;
        Message(ReplyStatus, "");
        Message(Status, "");
        RefreshHeader();
        RefreshActions();
    }

    private void CancelRequest()
    {
        var active = request;
        request = null;
        replying = false;
        active?.Cancel();
        RefreshActions();
    }

    private async Task Translate()
    {
        var text = selectedText ?? SourceText.Text;
        if (request is not null || string.IsNullOrWhiteSpace(text)) return;
        if (!Configured())
        {
            resumeTranslation = true;
            ShowSettings();
            Message(SettingsError, preferences.Language.Length == 0
                ? "Choisis d’abord ta langue." : "Ajoute une clé API ou choisis Codex.");
            if (preferences.Language.Length == 0) LanguageBox.Focus(); else KeyBox.Focus();
            return;
        }

        resumeTranslation = false;
        ResetResult();
        await RunTranslation(text, preferences, detectLanguage: selectedText is not null, forReply: false);
    }

    private async Task TranslateReply()
    {
        if (selectedText is null || request is not null || string.IsNullOrWhiteSpace(ReplyText.Text)) return;
        var target = sourceLanguage ?? ReplyLanguage.Text.Trim();
        if (!TranslationFormat.ValidLanguage(target))
        {
            Message(ReplyStatus, "Indique d’abord la langue d’origine.");
            ReplyLanguage.Focus();
            return;
        }

        ReplyResult.Clear();
        ReplyCard.Visibility = Visibility.Collapsed;
        await RunTranslation(ReplyText.Text, preferences with { Language = target }, detectLanguage: false, forReply: true);
    }

    private async Task RunTranslation(string text, Preferences settings, bool detectLanguage, bool forReply)
    {
        using var current = new CancellationTokenSource();
        request = current;
        replying = forReply;
        RefreshActions();
        var status = forReply ? ReplyStatus : Status;
        Message(status, "Traduction…");
        string? error = null;
        try
        {
            var key = settings.Provider == Provider.Codex ? null : Credentials.Get(settings.Provider);
            var result = await translator.TranslateAsync(text, settings, key, detectLanguage, current.Token);
            if (request != current) return;
            Message(status, "");
            if (forReply)
            {
                ReplyResult.Text = result.Text;
                ReplyCard.Visibility = Visibility.Visible;
            }
            else
            {
                ResultText.Text = result.Text;
                ResultCard.Visibility = Visibility.Visible;
                if (detectLanguage)
                {
                    sourceLanguage = result.SourceLanguage;
                    UnknownLanguage.Visibility = sourceLanguage is null ? Visibility.Visible : Visibility.Collapsed;
                    ReplyButton.ToolTip = sourceLanguage is null ? "Traduire la réponse (Entrée)"
                        : $"Traduire en {sourceLanguage.ToLowerInvariant()} (Entrée)";
                    ReplyPanel.Visibility = Visibility.Visible;
                    RefreshHeader();
                }
            }
        }
        catch (OperationCanceledException) { error = "Le service a mis trop de temps à répondre."; }
        catch (TranslationException e) { error = e.Message; }
        catch (HttpRequestException) { error = "Connexion impossible. Vérifie Internet."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or JsonException)
        {
            error = "Impossible de lire la réponse du fournisseur. Réessaie.";
        }
        finally
        {
            if (request == current)
            {
                request = null;
                replying = false;
                if (error is not null)
                {
                    Message(status, error);
                    if (!forReply && selectedText is not null) RetryButton.Visibility = Visibility.Visible;
                }
                RefreshActions();
            }
        }
    }

    private async void TranslateClicked(object sender, RoutedEventArgs e) => await Translate();
    private async void ReplyClicked(object sender, RoutedEventArgs e) => await TranslateReply();
    private void CopyClicked(object sender, RoutedEventArgs e) => Copy(ResultText.Text, Status);
    private void CopyReplyClicked(object sender, RoutedEventArgs e) => Copy(ReplyResult.Text, ReplyStatus);

    private void Copy(string text, TextBlock status)
    {
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            Message(status, "Copié");
            copyNotice.Stop();
            copyNotice.Start();
        }
        catch (ExternalException) { Message(status, "Presse-papiers occupé. Réessaie."); }
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && !LanguageBox.IsDropDownOpen && !ProviderBox.IsDropDownOpen && !ModelBox.IsDropDownOpen)
        {
            e.Handled = true;
            HidePanel();
        }
        else if (e.Key == Key.Enter && TranslateView.IsVisible && ReplyText.IsKeyboardFocusWithin &&
                 Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            e.Handled = true;
            await TranslateReply();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && TranslateView.IsVisible && selectedText is null)
        {
            e.Handled = true;
            await Translate();
        }
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        anchor = null;
        DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CancelRequest();
        modelRequest?.Cancel();
        base.OnClosing(e);
    }

    private void ClearSelection()
    {
        selectedText = null;
        anchor = null;
        resumeTranslation = false;
        ResetResult();
        if (Configured()) ShowTranslation();
    }

    private void HidePanel()
    {
        CancelRequest();
        modelRequest?.Cancel();
        if (selectedText is not null) ClearSelection();
        else Message(Status, "");
        Hide();
    }

    private void HideClicked(object sender, RoutedEventArgs e) => HidePanel();
}
