using System.Text.RegularExpressions;

namespace STranslate.Plugin.Bailian;

internal enum ThinkingKind
{
    Unknown,
    Budget,
    Qwen38,
    Always,
    Unsupported
}

internal sealed record ModelInfo(
    bool Coding = false,
    bool Token = false,
    bool Vision = false,
    ThinkingKind Thinking = ThinkingKind.Unknown,
    int? MaxThinkingTokens = null,
    bool MaxPixels = false,
    bool OcrOnly = false);

internal static class BailianConfig
{
    private static readonly Dictionary<string, ModelInfo> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen3.7-plus"] = new(true, true, true, ThinkingKind.Budget, 262144, true),
        ["qwen3.6-plus"] = new(true, false, true, ThinkingKind.Budget, 81920, true),
        ["qwen3.5-plus"] = new(true, false, true, ThinkingKind.Budget, 81920, true),
        ["kimi-k2.5"] = new(true, false, true, ThinkingKind.Budget, 81920),
        ["glm-5"] = new(true, false, false, ThinkingKind.Budget, 32768),
        ["minimax-m2.5"] = new(true, false, false, ThinkingKind.Always),
        ["qwen3-max-2026-01-23"] = new(true, false, false, ThinkingKind.Budget, 81920),
        ["qwen3-coder-next"] = new(true, false, false, ThinkingKind.Unsupported),
        ["qwen3-coder-plus"] = new(true, false, false, ThinkingKind.Unsupported),
        ["glm-4.7"] = new(true, false, false, ThinkingKind.Budget, 32768),
        ["qwen3.8-max"] = new(false, true, true, ThinkingKind.Qwen38, null, true),
        ["qwen3.8-max-preview"] = new(false, true, true, ThinkingKind.Qwen38, null, true),
        ["qwen3.8-flash"] = new(false, true, true, ThinkingKind.Qwen38, null, true),
        ["qwen3.7-max"] = new(false, true, false, ThinkingKind.Budget, 262144),
        ["qwen3.7-flash"] = new(false, false, true, ThinkingKind.Budget, 262144, true),
        ["qwen3.6-flash"] = new(false, true, true, ThinkingKind.Budget, 81920, true),
        ["qwen3.5-ocr"] = new(false, false, true, ThinkingKind.Unsupported, null, true, true),
        ["qwen-vl-ocr"] = new(false, false, true, ThinkingKind.Unsupported, null, true, true),
        ["qwen-mt-plus"] = new(false, false, false, ThinkingKind.Unsupported),
        ["qwen-mt-turbo"] = new(false, false, false, ThinkingKind.Unsupported),
        ["qwen-mt-flash"] = new(false, false, false, ThinkingKind.Unsupported),
        ["qwen-mt-lite"] = new(false, false, false, ThinkingKind.Unsupported)
    };

    public static IReadOnlyList<string> PresetModels { get; } =
    [
        "qwen3.7-plus", "qwen3.6-plus", "qwen3.5-plus", "kimi-k2.5", "glm-5",
        "MiniMax-M2.5", "qwen3-max-2026-01-23", "qwen3-coder-next", "qwen3-coder-plus", "glm-4.7",
        "qwen3.8-max", "qwen3.8-flash", "qwen3.7-max", "qwen3.6-flash", "qwen3.7-flash",
        "qwen-mt-plus", "qwen3.5-ocr", "qwen-vl-ocr"
    ];

    public static bool IsNativeCoordinateOcr(Settings settings) =>
        settings.AccessMode == BillingMode.PayAsYouGo &&
        settings.Model.Trim().ToLowerInvariant() is "qwen3.5-ocr" or "qwen-vl-ocr";

    public static bool SupportsCoordinateOcr(Settings settings)
    {
        if (IsNativeCoordinateOcr(settings)) return true;
        return !Models.TryGetValue(settings.Model.Trim(), out var info) || info.Vision;
    }

    public static bool IsOcrOnly(Settings settings) =>
        Models.TryGetValue(settings.Model.Trim(), out var info) && info.OcrOnly;

    public static string BillingModeLabel(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        BillingMode.PayAsYouGo => "按量付费",
        BillingMode.CodingPlan => "Coding Plan",
        BillingMode.TokenPlan => "Token Plan",
        _ => mode.Trim()
    };

    public static void Validate(Settings settings, bool ocr)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("API Key is required.");

        var mode = settings.AccessMode.Trim().ToLowerInvariant();
        if (mode is not (BillingMode.PayAsYouGo or BillingMode.CodingPlan or BillingMode.TokenPlan))
            throw new InvalidOperationException($"Unsupported billing mode: {settings.AccessMode}.");

        var model = settings.Model.Trim();
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException("Model is required.");

        Models.TryGetValue(model, out var info);
        if (mode == BillingMode.CodingPlan && (info is null || !info.Coding))
            throw new InvalidOperationException($"Model {model} is not in the plugin's verified Coding Plan model list.");
        if (mode == BillingMode.TokenPlan && (info is null || !info.Token))
            throw new InvalidOperationException($"Model {model} is not in the plugin's verified Token Plan model list.");
        if (ocr && info is not null && !info.Vision)
            throw new InvalidOperationException($"Model {model} does not support image input and cannot be used for OCR.");
        if (!ocr && info?.OcrOnly == true)
            throw new InvalidOperationException($"Model {model} is an OCR-only model and cannot be used for translation.");

        _ = ChatEndpoint(settings);
        _ = ThinkingFields(settings);
    }

    public static string ChatEndpoint(Settings settings)
    {
        var baseUrl = BaseUrl(settings);
        return baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/chat/completions";
    }

    public static string BaseUrl(Settings settings) => AutomaticBaseUrl(settings);

    public static string NativeOcrEndpoint(Settings settings)
    {
        if (settings.AccessMode != BillingMode.PayAsYouGo)
            throw new InvalidOperationException("Native coordinate OCR requires pay-as-you-go.");

        var region = NormalizeRegion(settings.Region);
        var host = string.IsNullOrWhiteSpace(settings.WorkspaceId)
            ? region == "china" ? "dashscope.aliyuncs.com" : "dashscope-intl.aliyuncs.com"
            : $"{ValidateWorkspaceId(settings.WorkspaceId)}.{(region == "china" ? "cn-beijing" : "ap-southeast-1")}.maas.aliyuncs.com";
        return $"https://{host}/api/v1/services/aigc/multimodal-generation/generation";
    }

    public static Dictionary<string, object?> ThinkingFields(Settings settings)
    {
        Models.TryGetValue(settings.Model.Trim(), out var info);
        var family = info?.Thinking ?? ThinkingKind.Unknown;
        var effort = settings.ReasoningEffort.Trim().ToLowerInvariant();
        if (effort is not ("auto" or "low" or "medium" or "high"))
            throw new InvalidOperationException($"Unsupported reasoning effort: {settings.ReasoningEffort}.");

        if (family == ThinkingKind.Unknown)
        {
            if (settings.EnableThinking)
                throw new InvalidOperationException($"Model {settings.Model} is not in the plugin's thinking compatibility table.");
            return [];
        }
        if (family == ThinkingKind.Unsupported)
        {
            if (settings.EnableThinking)
                throw new InvalidOperationException($"Model {settings.Model} is not configured for selectable thinking mode.");
            return [];
        }
        if (family == ThinkingKind.Always)
        {
            if (!settings.EnableThinking)
                throw new InvalidOperationException($"Model {settings.Model} is always-thinking; turn on Enable thinking to use it.");
            if (effort != "auto")
                throw new InvalidOperationException($"Model {settings.Model} does not support configurable reasoning effort.");
            return [];
        }
        if (family == ThinkingKind.Qwen38)
        {
            if (!settings.EnableThinking) return new() { ["reasoning_effort"] = "none" };
            if (effort == "auto") return [];
            return new() { ["reasoning_effort"] = effort == "high" ? "xhigh" : effort };
        }

        var fields = new Dictionary<string, object?> { ["enable_thinking"] = settings.EnableThinking };
        if (!settings.EnableThinking || effort == "auto") return fields;
        var maximum = info?.MaxThinkingTokens ?? throw new InvalidOperationException($"Model {settings.Model} does not support configurable reasoning effort.");
        fields["thinking_budget"] = effort switch
        {
            "low" => Math.Min(4096, maximum),
            "medium" => Math.Min(16384, maximum),
            _ => maximum
        };
        return fields;
    }

    public static bool SupportsMaxPixels(string model) => Models.TryGetValue(model.Trim(), out var info) && info.MaxPixels;

    private static string AutomaticBaseUrl(Settings settings)
    {
        var mode = settings.AccessMode.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(settings.CustomBaseUrl))
        {
            if (mode != BillingMode.PayAsYouGo)
                throw new InvalidOperationException("Coding Plan and Token Plan must use their official Base URLs; remove the custom Base URL.");
            return ValidateHttpsUrl(settings.CustomBaseUrl);
        }

        if (mode == BillingMode.CodingPlan) return "https://coding.dashscope.aliyuncs.com/v1";
        if (mode == BillingMode.TokenPlan) return "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";

        var region = NormalizeRegion(settings.Region);
        if (string.IsNullOrWhiteSpace(settings.WorkspaceId))
            return region == "china"
                ? "https://dashscope.aliyuncs.com/compatible-mode/v1"
                : "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";

        var deployment = region == "china" ? "cn-beijing" : "ap-southeast-1";
        return $"https://{ValidateWorkspaceId(settings.WorkspaceId)}.{deployment}.maas.aliyuncs.com/compatible-mode/v1";
    }

    private static string NormalizeRegion(string region)
    {
        var value = region.Trim().ToLowerInvariant();
        if (value is not ("china" or "singapore"))
            throw new InvalidOperationException($"Unsupported region: {region}.");
        return value;
    }

    private static string ValidateWorkspaceId(string workspaceId)
    {
        var value = workspaceId.Trim();
        if (!Regex.IsMatch(value, "^[a-z0-9-]+$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Workspace ID may contain only letters, numbers, and hyphens.");
        return value;
    }

    private static string ValidateHttpsUrl(string input)
    {
        var value = input.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Custom Base URL must be an HTTPS URL without credentials, query, or fragment.");
        return value;
    }
}
