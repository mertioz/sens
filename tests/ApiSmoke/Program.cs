using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sens;

if (args.Contains("--codex-unconfigured"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var testPrefix = Environment.GetEnvironmentVariable("SENS_TEST_CODEX_PREFIX");
    if (!string.IsNullOrEmpty(testPrefix))
    {
        var executable = CodexLocator.Find("").FirstOrDefault();
        Require(executable is not null && executable.StartsWith(testPrefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Codex doit être découvert dans le préfixe npm de test");
    }
    try
    {
        await new CodexBridge().ReadModelsAsync(timeout.Token);
        throw new Exception("Le test attend une installation Codex sans compte connecté.");
    }
    catch (TranslationException error) when (error.Message.Contains("codex login", StringComparison.Ordinal))
    {
        Console.WriteLine("OK : Codex détecté dans un préfixe npm personnalisé. La connexion est proposée.");
    }
    return;
}

if (args.Contains("--codex"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(85));
    var bridge = new CodexBridge();
    var models = await bridge.ReadModelsAsync(timeout.Token);
    Console.WriteLine($"Modèles Codex disponibles : {string.Join(", ", models.Select(m => m.Label))}");
    var model = models.FirstOrDefault(m => m.Id == "gpt-6-luna")?.Id ?? "";
    var prefs = new Preferences("Français", Provider.Codex, model, StylePrompt: "Adopte un ton naturel et chaleureux.");
    using var http = new HttpClient();
    var translator = new Translator(http, bridge);
    var incoming = await translator.TranslateAsync("I can meet you at the train station at noon.", prefs, null, true, timeout.Token);
    Require(incoming.SourceLanguage == "Anglais" && incoming.Text.Contains("midi"), "Traduction et détection Codex");
    var reply = await translator.TranslateAsync("Parfait, je te retrouve devant la gare à midi.", prefs with { Language = incoming.SourceLanguage! }, null, false, timeout.Token);
    Require(reply.Text.Contains("noon", StringComparison.OrdinalIgnoreCase), "Réponse traduite par Codex en anglais");
    Console.WriteLine("OK : traduction et réponse via la session Codex ChatGPT.");
    return;
}

const string input = "It's raining cats and dogs.\nI'll be there in 10.";
const string style = "Ton chaleureux, phrases courtes, tutoiement.";
foreach (var provider in new[] { Provider.OpenAI, Provider.DeepSeek })
{
    var openAi = provider == Provider.OpenAI;
    using var http = new HttpClient(new Reply(async (request, ct) =>
    {
        Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == (openAi
            ? "https://api.openai.com/v1/responses" : "https://api.deepseek.com/chat/completions"), "Route fournisseur");
        Require(request.Headers.Authorization?.Scheme == "Bearer" &&
                request.Headers.Authorization.Parameter == "test-placeholder", "En-tête d’authentification");
        using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var data = parsed.RootElement;
        if (openAi)
        {
            Require(!data.GetProperty("store").GetBoolean(), "Pas de stockage de réponse");
            Require(data.GetProperty("instructions").GetString()!.Contains("Français"), "Langue cible");
            Require(data.GetProperty("instructions").GetString()!.Contains(style), "Consigne de style OpenAI");
            Require(data.GetProperty("input").GetString() == input, "Texte source intact");
        }
        else
        {
            var messages = data.GetProperty("messages");
            Require(messages[0].GetProperty("role").GetString() == "system" &&
                    messages[0].GetProperty("content").GetString()!.Contains("Français"), "Langue cible");
            Require(messages[0].GetProperty("content").GetString()!.Contains(style), "Consigne de style DeepSeek");
            Require(messages[1].GetProperty("content").GetString() == input, "Texte source intact");
        }

        return Json(openAi
            ? """{"status":"completed","output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"Il pleut des cordes.\nJ’arrive dans dix minutes."}]}]}"""
            : """{"choices":[{"finish_reason":"stop","message":{"content":"Il pleut des cordes.\nJ’arrive dans dix minutes."}}]}""");
    }));
    var result = await new Translator(http, new CodexBridge()).TranslateAsync(input,
        new("Français", provider, StylePrompt: style), "test-placeholder", false, default);
    Require(result.Text == "Il pleut des cordes.\nJ’arrive dans dix minutes.", "Extraction de la traduction");
}
foreach (var provider in new[] { Provider.OpenAI, Provider.DeepSeek })
{
    bool openAi = provider == Provider.OpenAI;
    using var http = new HttpClient(new Reply(async (request, ct) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var format = openAi ? body.RootElement.GetProperty("text").GetProperty("format")
            : body.RootElement.GetProperty("response_format");
        Require(format.GetProperty("type").GetString() == (openAi ? "json_schema" : "json_object"), "Format de détection");
        var content = JsonSerializer.Serialize(new { translation = "Il pleut des cordes.", source_language = "Anglais" });
        return Json(JsonSerializer.Serialize(openAi
            ? (object)new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = content } } } } }
            : new { choices = new[] { new { finish_reason = "stop", message = new { content } } } }));
    }));
    var result = await new Translator(http, new CodexBridge()).TranslateAsync(input, new("Français", provider), "test-placeholder", true, default);
    Require(result.SourceLanguage == "Anglais" && result.Text == "Il pleut des cordes.", "Détection de la langue du texte d’origine");
}
Require(TranslationFormat.Parse("""{"translation":"OK","source_language":null}""", true).SourceLanguage is null,
    "Ne pas choisir une langue pour un texte ambigu");
foreach (var detectLanguage in new[] { false, true })
{
    using var http = new HttpClient(new Reply(async (request, ct) =>
    {
        Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://api.anthropic.com/v1/messages",
            "Route Claude Messages");
        Require(request.Headers.Authorization?.Parameter == "test-placeholder" &&
                request.Headers.GetValues("anthropic-version").Single() == "2026-10-21", "En-têtes Claude");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var body = json.RootElement;
        Require(body.GetProperty("model").GetString() == "claude-haiku-5-5", "Modèle Claude par défaut");
        Require(body.GetProperty("system").GetString()!.Contains(style), "Consigne de style Claude");
        var message = body.GetProperty("messages")[0];
        Require(message.GetProperty("role").GetString() == "user" && message.GetProperty("content").GetString() == input,
            "Claude reçoit le texte source dans le message utilisateur");
        Require(body.TryGetProperty("output_config", out var output) == detectLanguage &&
                (!detectLanguage || output.GetProperty("format").GetProperty("type").GetString() == "json_schema"),
            "Format de détection Claude");
        var text = detectLanguage ? """{"translation":"Il pleut des cordes.","source_language":"Anglais"}""" : "Il pleut des cordes.";
        return Json(JsonSerializer.Serialize(new
        {
            stop_reason = "end_turn",
            content = new[] { new { type = "text", text } }
        }));
    }));
    var prefs = new Preferences("Français", Provider.Claude, StylePrompt: style);
    var result = await new Translator(http, new CodexBridge()).TranslateAsync(input, prefs, "test-placeholder", detectLanguage, default);
    Require(result.Text == "Il pleut des cordes." && result.SourceLanguage == (detectLanguage ? "Anglais" : null),
        "Claude renvoie la traduction et, si demandée, la langue d’origine");
}
using (var http = new HttpClient(new Reply((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))))
{
    try
    {
        await new Translator(http, new CodexBridge()).TranslateAsync(input, new("Français"), "test-placeholder", false, default);
        throw new Exception("Une réponse 401 doit signaler une clé refusée.");
    }
    catch (TranslationException e) when (e.Message.Contains("Clé API refusée")) { }
}
Console.WriteLine("OK : requêtes et réponses simulées OpenAI, DeepSeek et Claude, détection de langue et erreur de clé API.");

var fixture = Directory.CreateTempSubdirectory("sens portable ");
try
{
    var standalone = Path.Combine(fixture.FullName, "Codex standalone", "bin", "codex.exe");
    var npmPrefix = Path.Combine(fixture.FullName, "npm personnalisé");
    var npmPackage = Path.Combine(npmPrefix, "node_modules", "@openai", "codex", "node_modules", "@openai");
    var npm = Path.Combine(npmPackage, "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
    var arm = Path.Combine(npmPackage, "codex-win32-arm64", "vendor", "aarch64-pc-windows-msvc", "bin", "codex.exe");
    foreach (var path in new[] { standalone, npm, arm })
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }
    Require(CodexLocator.Search([Path.GetDirectoryName(standalone)!, npmPrefix], Architecture.X64)
        .SequenceEqual([standalone, npm]), "Détection autonome et npm sur Windows x64");
    Require(CodexLocator.Search([npmPrefix], Architecture.Arm64).SequenceEqual([arm, npm]), "Détection ARM64 avec repli x64");
    Require(CodexLocator.Find(npm).SequenceEqual([npm]), "Sélection manuelle de codex.exe");
    Require(CodexLocator.Search([".", Path.Combine(fixture.FullName, "absent")], Architecture.X64).Count == 0,
        "Absence de Codex et chemins relatifs ignorés");
}
finally { fixture.Delete(recursive: true); }
Console.WriteLine("OK : détection de Codex avec des chemins Windows indépendants du poste de développement.");

static void Require(bool assertion, string message)
{
    if (!assertion) throw new Exception(message);
}
static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};
sealed class Reply(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}
