using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using STranslate.Plugin;

namespace STranslate.Plugin.Bailian;

internal static class BailianProtocol
{
    internal const int MaxImageDataUrlChars = 20 * 1024 * 1024;
    private const string TranslationInstruction = "You are a professional translation engine. Translate faithfully and naturally, preserving paragraphs, line breaks, lists, punctuation, names, numbers, and technical terms. Return only the translated text without explanations, labels, quotes, or Markdown fences.";
    private const string OcrInstruction = "You are a precise OCR and text-localization engine. Locate and transcribe every visible text line without translating, summarizing, correcting, or inventing content. Return only the requested JSON object without Markdown fences or commentary.";

    public static Dictionary<string, object?> TranslationRequest(
        Settings settings,
        string text,
        string source,
        string target,
        Prompt? prompt = null)
    {
        var model = settings.Model.Trim();
        var body = RequestBase(settings, 4096);
        if (model.StartsWith("qwen-mt-", StringComparison.OrdinalIgnoreCase))
        {
            body["messages"] = new[] { new Dictionary<string, object?> { ["role"] = "user", ["content"] = text } };
            body["translation_options"] = new Dictionary<string, object?>
            {
                ["source_lang"] = source == "Auto detect" ? "auto" : source,
                ["target_lang"] = target == "Auto detect" ? "English" : target
            };
            if (model.Equals("qwen-mt-plus", StringComparison.OrdinalIgnoreCase) ||
                model.Equals("qwen-mt-turbo", StringComparison.OrdinalIgnoreCase))
                body["stream"] = false;
            return body;
        }

        body["messages"] = prompt is null || prompt.Items.Count == 0
            ? new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = TranslationInstruction },
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = $"Source language: {source}\nTarget language: {target}\nTranslate the text below. Return only the translation.\n\n{text}"
                }
            }
            : prompt.Items.Select(item => (object)new Dictionary<string, object?>
            {
                ["role"] = item.Role,
                ["content"] = item.Content
                    .Replace("$source", source, StringComparison.Ordinal)
                    .Replace("$target", target, StringComparison.Ordinal)
                    .Replace("$content", text, StringComparison.Ordinal)
            }).ToArray();
        return body;
    }

    public static Dictionary<string, object?> GenericOcrRequest(Settings settings, byte[] imageData, string expectedLanguage)
    {
        var body = RequestBase(settings, 8192);
        var image = new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?> { ["url"] = ImageDataUrl(imageData) }
        };
        var maxPixels = ResolutionMaxPixels(settings);
        if (maxPixels is not null)
        {
            if (!BailianConfig.SupportsMaxPixels(settings.Model))
                throw new InvalidOperationException($"Model {settings.Model} does not support the OCR resolution setting.");
            image["max_pixels"] = maxPixels;
        }

        var userMessage = new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = new object[]
            {
                image,
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = $"Expected language: {expectedLanguage}.\nLocate and transcribe every visible text line in reading order. Return only compact JSON with this exact shape: {{\"items\":[{{\"text\":\"recognized line\",\"box\":[x1,y1,x2,y2]}}]}}. Each box is the tight axis-aligned boundary of that line. Coordinates must be integers normalized to 0..999 relative to the full image: top-left is (0,0), bottom-right is (999,999). Use one item per visual text line. Do not translate, explain, correct, or use Markdown. If no text is visible, return {{\"items\":[]}}."
                }
            }
        };
        body["stream"] = false;
        body["messages"] = settings.Model.Equals("qwen3.5-ocr", StringComparison.OrdinalIgnoreCase)
            ? new object[] { userMessage }
            : new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = OcrInstruction },
                userMessage
            };
        return body;
    }

    public static Dictionary<string, object?> NativeOcrRequest(Settings settings, byte[] imageData)
    {
        var image = new Dictionary<string, object?>
        {
            ["image"] = ImageDataUrl(imageData),
            ["min_pixels"] = 3072,
            ["enable_rotate"] = false
        };
        var maxPixels = ResolutionMaxPixels(settings);
        if (maxPixels is not null) image["max_pixels"] = maxPixels;

        return new Dictionary<string, object?>
        {
            ["model"] = settings.Model.Trim(),
            ["input"] = new Dictionary<string, object?>
            {
                ["messages"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new object[] { image }
                    }
                }
            },
            ["parameters"] = new Dictionary<string, object?>
            {
                ["ocr_options"] = new Dictionary<string, object?> { ["task"] = "advanced_recognition" }
            }
        };
    }

    public static Options RequestOptions(Settings settings) => new()
    {
        Headers = new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {settings.ApiKey.Trim()}"
        }
    };

    public static string ParseCompletion(string response)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(response) ?? throw new InvalidOperationException();
        }
        catch
        {
            throw new InvalidOperationException("Model Studio returned an invalid JSON response.");
        }

        var choice = root["choices"]?[0];
        if (choice is null) throw ApiError(root, "Model Studio response did not include a completion choice.");
        if (choice["finish_reason"]?.GetValue<string>() == "length")
            throw new InvalidOperationException("Model Studio output was truncated; increase Max tokens or reduce the input.");
        var content = ReadText(choice["message"]?["content"]);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Model Studio response did not include result text.");
        return content;
    }

    public static string ParseSseChunk(string chunk, out bool finishedByLength)
    {
        finishedByLength = false;
        if (string.IsNullOrWhiteSpace(chunk)) return string.Empty;
        var output = new StringBuilder();
        foreach (var rawLine in chunk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(':')) continue;
            var payload = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? line[5..].Trim() : line;
            if (payload == "[DONE]") continue;
            JsonNode? root;
            try { root = JsonNode.Parse(payload); }
            catch { continue; }
            if (root?["error"] is not null) throw ApiError(root, "Model Studio returned an error.");
            var choice = root?["choices"]?[0];
            if (choice is null) continue;
            if (choice["finish_reason"]?.GetValue<string>() == "length") finishedByLength = true;
            output.Append(ReadText(choice["delta"]?["content"]));
        }
        return output.ToString();
    }

    public static OcrResult ParseNativeOcr(string response)
    {
        JsonNode root;
        try
        {
            root = JsonNode.Parse(response) ?? throw new InvalidOperationException();
        }
        catch
        {
            throw new InvalidOperationException("Model Studio returned an invalid OCR response.");
        }

        var content = root["output"]?["choices"]?[0]?["message"]?["content"] as JsonArray;
        if (content is null) throw ApiError(root, "Model Studio OCR response did not include content.");
        var result = new OcrResult();
        var fallbackText = new List<string>();
        foreach (var item in content)
        {
            var words = item?["ocr_result"]?["words_info"] as JsonArray;
            if (words is not null)
            {
                foreach (var word in words)
                {
                    var text = word?["text"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    fallbackText.Add(text);
                    var ocrContent = new OcrContent { Text = text };
                    foreach (var point in Coordinates(word)) ocrContent.BoxPoints.Add(point);
                    result.OcrContents.Add(ocrContent);
                }
            }
            else
            {
                var text = ReadText(item?["text"]);
                if (!string.IsNullOrWhiteSpace(text)) fallbackText.Add(text.Trim());
            }
        }

        if (result.OcrContents.Count == 0)
        {
            AddTextLines(result, string.Join("\n", fallbackText));
        }
        else if (result.OcrContents.All(item => item.BoxPoints.Count == 0))
        {
            var text = string.Join("\n", result.OcrContents.Select(item => item.Text));
            result.OcrContents.Clear();
            AddTextLines(result, text);
        }
        return result;
    }

    public static OcrResult ParseGenericOcr(string response, int pixelWidth, int pixelHeight)
    {
        var completion = ParseCompletion(response);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(StripJsonFence(completion));
        }
        catch
        {
            return TextOcrResult(completion);
        }

        var items = root as JsonArray ?? root?["items"] as JsonArray;
        if (items is null) return TextOcrResult(completion);

        var result = new OcrResult();
        foreach (var item in items)
        {
            var text = item?["text"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var content = new OcrContent { Text = text };
            foreach (var point in NormalizedCoordinates(item?["box"] ?? item?["bbox"] ?? item?["bbox_2d"], pixelWidth, pixelHeight))
                content.BoxPoints.Add(point);
            result.OcrContents.Add(content);
        }
        return result;
    }

    public static OcrResult TextOcrResult(string text)
    {
        var result = new OcrResult();
        AddTextLines(result, text);
        return result;
    }

    public static string Redact(string? message, string? apiKey)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "Unknown Model Studio error." : message;
        if (!string.IsNullOrWhiteSpace(apiKey)) value = value.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        return value.Length > 2000 ? value[..2000] : value;
    }

    private static Dictionary<string, object?> RequestBase(Settings settings, int fallbackMaxTokens)
    {
        var maxTokens = settings.MaxTokens > 0 ? Math.Min(settings.MaxTokens, 262144) : fallbackMaxTokens;
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.Model.Trim(),
            ["messages"] = Array.Empty<object>(),
            ["temperature"] = 0.1,
            ["max_tokens"] = maxTokens,
            ["stream"] = settings.Stream
        };
        foreach (var field in BailianConfig.ThinkingFields(settings)) body[field.Key] = field.Value;
        return body;
    }

    private static int? ResolutionMaxPixels(Settings settings) => settings.OcrResolution.Trim().ToLowerInvariant() switch
    {
        "auto" => null,
        "fast" => 1048576,
        "high" => 8388608,
        _ => throw new InvalidOperationException($"Unsupported OCR resolution: {settings.OcrResolution}.")
    };

    private static string ImageDataUrl(byte[] data)
    {
        if (data.Length == 0) throw new InvalidOperationException("Image content is required.");
        var mime = DetectMime(data);
        var estimatedLength = $"data:{mime};base64,".Length + ((data.Length + 2L) / 3L * 4L);
        if (estimatedLength > MaxImageDataUrlChars)
            throw new InvalidOperationException("Image data exceeds the 20 MB Data URL limit.");
        return $"data:{mime};base64,{Convert.ToBase64String(data)}";
    }

    private static string DetectMime(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return "image/jpeg";
        if (data.Length >= 6 && Encoding.ASCII.GetString(data, 0, 3) == "GIF") return "image/gif";
        if (data.Length >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WEBP") return "image/webp";
        return "image/png";
    }

    private static string ReadText(JsonNode? node)
    {
        if (node is null) return string.Empty;
        if (node is JsonValue) return node.GetValue<string>();
        if (node is not JsonArray array) return string.Empty;
        return string.Concat(array.Select(item => item?["text"]?.GetValue<string>() ?? string.Empty));
    }

    private static string StripJsonFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0) return text;
        text = text[(firstLineEnd + 1)..];
        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFence >= 0 ? text[..closingFence] : text).Trim();
    }

    private static IEnumerable<BoxPoint> NormalizedCoordinates(JsonNode? node, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || !TryCoordinateNumbers(node, out var values)) yield break;

        float[] points = values.Length switch
        {
            4 => [values[0], values[1], values[2], values[1], values[2], values[3], values[0], values[3]],
            8 => values,
            _ => []
        };
        if (points.Length == 0) yield break;

        var xs = points.Where((_, index) => index % 2 == 0).ToArray();
        var ys = points.Where((_, index) => index % 2 == 1).ToArray();
        if (xs.Max() <= xs.Min() || ys.Max() <= ys.Min()) yield break;

        for (var index = 0; index < points.Length; index += 2)
        {
            var x = Math.Clamp(points[index], 0f, 999f) / 999f * pixelWidth;
            var y = Math.Clamp(points[index + 1], 0f, 999f) / 999f * pixelHeight;
            yield return new BoxPoint(x, y);
        }
    }

    private static bool TryCoordinateNumbers(JsonNode? node, out float[] values)
    {
        values = [];
        if (node is not JsonArray array) return false;
        var flattened = array.Count == 4 && array.All(item => item is JsonArray)
            ? array.SelectMany(item => (JsonArray)item!).ToArray()
            : array.ToArray();
        if (flattened.Length is not (4 or 8)) return false;

        var parsed = new float[flattened.Length];
        for (var index = 0; index < flattened.Length; index++)
        {
            if (!float.TryParse(flattened[index]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index]) || !float.IsFinite(parsed[index]))
                return false;
        }
        values = parsed;
        return true;
    }

    private static IEnumerable<BoxPoint> Coordinates(JsonNode? word)
    {
        if (TryNumbers(word?["location"], 8, out var location))
        {
            yield return new BoxPoint(location[0], location[1]);
            yield return new BoxPoint(location[2], location[3]);
            yield return new BoxPoint(location[4], location[5]);
            yield return new BoxPoint(location[6], location[7]);
            yield break;
        }
        if (!TryNumbers(word?["rotate_rect"], 5, out var rect)) yield break;
        var cx = rect[0];
        var cy = rect[1];
        var halfWidth = rect[2] / 2f;
        var halfHeight = rect[3] / 2f;
        var radians = rect[4] * MathF.PI / 180f;
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        foreach (var (x, y) in new[] { (-halfWidth, -halfHeight), (halfWidth, -halfHeight), (halfWidth, halfHeight), (-halfWidth, halfHeight) })
            yield return new BoxPoint(cx + x * cosine - y * sine, cy + x * sine + y * cosine);
    }

    private static bool TryNumbers(JsonNode? node, int count, out float[] values)
    {
        values = [];
        if (node is not JsonArray array || array.Count < count) return false;
        var parsed = new float[count];
        for (var index = 0; index < count; index++)
        {
            if (!float.TryParse(array[index]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[index]) || !float.IsFinite(parsed[index]))
                return false;
        }
        values = parsed;
        return true;
    }

    private static void AddTextLines(OcrResult result, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) result.OcrContents.Add(new OcrContent { Text = line });
    }

    private static InvalidOperationException ApiError(JsonNode root, string fallback)
    {
        var message = root["error"]?["message"]?.GetValue<string>()
            ?? root["message"]?.GetValue<string>()
            ?? root["code"]?.GetValue<string>();
        return new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? fallback : message);
    }
}
