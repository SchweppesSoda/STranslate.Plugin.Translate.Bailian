using STranslate.Plugin;

namespace STranslate.Plugin.Bailian;

public sealed class Settings
{
    public string AccessMode { get; set; } = BillingMode.PayAsYouGo;
    public string Region { get; set; } = "china";
    public string WorkspaceId { get; set; } = string.Empty;
    public string CustomBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "qwen3.7-plus";
    public int MaxTokens { get; set; } = 8192;
    public bool Stream { get; set; } = true;
    public bool EnableThinking { get; set; }
    public string ReasoningEffort { get; set; } = "auto";
    public string OcrResolution { get; set; } = "auto";
    public List<Prompt> Prompts { get; set; } = DefaultPrompts();

    public static List<Prompt> DefaultPrompts() =>
    [
        new Prompt(
            "Professional translation",
            [
                new PromptItem(
                    "system",
                    "You are a professional translation engine. Translate faithfully and naturally, preserving paragraphs, line breaks, lists, punctuation, names, numbers, and technical terms. Return only the translated text without explanations, labels, quotes, or Markdown fences."),
                new PromptItem(
                    "user",
                    "Source language: $source\nTarget language: $target\nTranslate the text below. Return only the translation.\n\n$content")
            ],
            true)
    ];
}

internal static class BillingMode
{
    public const string PayAsYouGo = "pay_as_you_go";
    public const string CodingPlan = "coding_plan";
    public const string TokenPlan = "token_plan";
}
