namespace Sens;

internal sealed record ModelChoice(string Id, string Label, string? Effort = null)
{
    internal static readonly ModelChoice Automatic = new("", "Automatique");
    private static readonly ModelChoice[] OpenAi =
    [
        new("gpt-4.1-mini", "GPT-4.1 mini"),
        new("gpt-6-luna", "GPT-6 Luna", "none"),
        new("gpt-6.1-sol", "GPT-6.1 Sol", "low"),
        new("gpt-6.1-terra", "GPT-6.1 Terra", "none"),
        new("gpt-6-astra", "GPT-6 Astra", "low")
    ];
    private static readonly ModelChoice[] DeepSeek =
    [
        new("deepseek-flash", "DeepSeek Flash"),
        new("deepseek-v4-pro", "DeepSeek V4 Pro")
    ];
    private static readonly ModelChoice[] Claude =
    [
        new("claude-haiku-5-5", "Claude Haiku 5.5"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        new("claude-opus-5-5", "Claude Opus 5.5")
    ];

    public static IReadOnlyList<ModelChoice> For(Provider provider) => provider switch
    {
        Provider.OpenAI => OpenAi,
        Provider.DeepSeek => DeepSeek,
        Provider.Claude => Claude,
        _ => [Automatic]
    };
    public static ModelChoice Resolve(Preferences settings) =>
        For(settings.Provider).FirstOrDefault(m => m.Id == settings.Model) ?? For(settings.Provider)[0];
}
