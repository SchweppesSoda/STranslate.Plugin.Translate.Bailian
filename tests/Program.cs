using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using STranslate.Plugin;
using STranslate.Plugin.Bailian;

var tests = new (string Name, Func<Task> Run)[]
{
    ("three billing endpoints", TestBillingEndpoints),
    ("settings save and connection test", TestSettingsSaveAndConnection),
    ("chat and Qwen-MT requests", TestTranslationRequests),
    ("normal translation response", TestCompletion),
    ("line-wise SSE and reasoning filtering", TestSse),
    ("line-wise SSE translation", TestStreamingTranslation),
    ("generic OCR request", TestGenericOcr),
    ("location OCR coordinates", TestLocation),
    ("rotate_rect OCR coordinates", TestRotateRect),
    ("coordinate-free OCR fallback", TestOcrFallback),
    ("translation error redaction", TestErrorRedaction),
    ("translation cancellation", TestCancellation),
    ("combined translation and OCR class", TestInterfaces),
    ("release package layout", TestPackage)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (failures > 0) Environment.Exit(1);
Console.WriteLine($"{tests.Length} STranslate tests passed.");

static Task TestBillingEndpoints()
{
    var settings = ValidSettings();
    Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", BailianConfig.BaseUrl(settings));
    Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", BailianConfig.ChatEndpoint(settings));
    settings.AccessMode = BillingMode.CodingPlan;
    Equal("https://coding.dashscope.aliyuncs.com/v1", BailianConfig.BaseUrl(settings));
    Equal("https://coding.dashscope.aliyuncs.com/v1/chat/completions", BailianConfig.ChatEndpoint(settings));
    settings.AccessMode = BillingMode.TokenPlan;
    Equal("https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", BailianConfig.BaseUrl(settings));
    Equal("https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions", BailianConfig.ChatEndpoint(settings));
    return Task.CompletedTask;
}

static async Task TestSettingsSaveAndConnection()
{
    var settings = ValidSettings();
    settings.AccessMode = BillingMode.CodingPlan;
    var context = ContextProxy.Create(settings);
    var main = new Main();
    main.Init(context);

    main.SaveSettings();
    Equal(1, ContextProxy.For(context).SaveCount);

    var message = await main.TestConnectionAsync();
    Equal(2, ContextProxy.For(context).SaveCount);
    Equal("连接成功：Coding Plan · qwen3.7-plus", message);
    Equal("https://coding.dashscope.aliyuncs.com/v1/chat/completions", ContextProxy.For(context).Http.LastUrl);
    Equal(false, ((Dictionary<string, object?>)ContextProxy.For(context).Http.LastBody!)["stream"]);
}

static Task TestTranslationRequests()
{
    var settings = ValidSettings();
    var chat = BailianProtocol.TranslationRequest(settings, "hello", "English", "Simplified Chinese");
    Equal(true, chat["stream"]);
    var chatJson = JsonSerializer.Serialize(chat);
    Contains("$content", chatJson, false);
    Contains("hello", chatJson);

    settings.Model = "qwen-mt-plus";
    var mt = BailianProtocol.TranslationRequest(settings, "hello", "Auto detect", "Simplified Chinese");
    Equal(false, mt["stream"]);
    var options = (Dictionary<string, object?>)mt["translation_options"]!;
    Equal("auto", options["source_lang"]);
    Equal("Simplified Chinese", options["target_lang"]);
    return Task.CompletedTask;
}

static Task TestCompletion()
{
    Equal("你好", BailianProtocol.ParseCompletion("{\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}]}"));
    return Task.CompletedTask;
}

static Task TestSse()
{
    var output = BailianProtocol.ParseSseChunk(
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"secret\",\"role\":\"assistant\"}}]}",
        out var firstTruncated);
    output += BailianProtocol.ParseSseChunk(
        "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}",
        out var secondTruncated);
    output += BailianProtocol.ParseSseChunk(
        "data: {\"choices\":[{\"delta\":{\"content\":\"好\"},\"finish_reason\":\"stop\"}]}",
        out var thirdTruncated);
    output += BailianProtocol.ParseSseChunk("data: [DONE]", out var doneTruncated);
    Equal("你好", output);
    Equal(false, firstTruncated || secondTruncated || thirdTruncated || doneTruncated);
    return Task.CompletedTask;
}

static async Task TestStreamingTranslation()
{
    var settings = ValidSettings();
    var main = new Main();
    main.Init(ContextProxy.Create(settings, streamChunks:
    [
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}",
        "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}",
        "data: {\"choices\":[{\"delta\":{\"content\":\"好\"},\"finish_reason\":\"stop\"}]}",
        "data: [DONE]"
    ]));
    var result = new TranslateResult();
    await main.TranslateAsync(
        new TranslateRequest("hello", LangEnum.English, LangEnum.ChineseSimplified),
        result);
    Equal(true, result.IsSuccess);
    Equal("你好", result.Text);
}

static Task TestGenericOcr()
{
    var settings = ValidSettings();
    settings.AccessMode = BillingMode.CodingPlan;
    settings.OcrResolution = "high";
    var request = BailianProtocol.GenericOcrRequest(settings, PngBytes(), "English");
    Equal(false, request["stream"]);
    var json = JsonSerializer.Serialize(request);
    Contains("data:image/png;base64,", json);
    Contains("8388608", json);
    return Task.CompletedTask;
}

static Task TestLocation()
{
    var result = BailianProtocol.ParseNativeOcr(NativeOcrJson("\"location\":[10,20,30,20,30,40,10,40]"));
    Equal(1, result.OcrContents.Count);
    Equal(4, result.OcrContents[0].BoxPoints.Count);
    Near(10, result.OcrContents[0].BoxPoints[0].X);
    Near(40, result.OcrContents[0].BoxPoints[2].Y);
    return Task.CompletedTask;
}

static Task TestRotateRect()
{
    var result = BailianProtocol.ParseNativeOcr(NativeOcrJson("\"rotate_rect\":[50,40,20,10,0]"));
    Equal(4, result.OcrContents[0].BoxPoints.Count);
    Near(40, result.OcrContents[0].BoxPoints[0].X);
    Near(35, result.OcrContents[0].BoxPoints[0].Y);
    Near(60, result.OcrContents[0].BoxPoints[2].X);
    return Task.CompletedTask;
}

static Task TestOcrFallback()
{
    var result = BailianProtocol.ParseNativeOcr(NativeOcrJson("\"confidence\":0.99"));
    Equal(1, result.OcrContents.Count);
    Equal("line", result.OcrContents[0].Text);
    Equal(0, result.OcrContents[0].BoxPoints.Count);
    return Task.CompletedTask;
}

static async Task TestErrorRedaction()
{
    var settings = ValidSettings();
    settings.Stream = false;
    settings.ApiKey = "sk-secret";
    var main = new Main();
    main.Init(ContextProxy.Create(settings, postResponse: "{\"error\":{\"message\":\"bad sk-secret\"}}"));
    var result = new TranslateResult();
    await main.TranslateAsync(new TranslateRequest("hello", LangEnum.English, LangEnum.ChineseSimplified), result);
    Equal(false, result.IsSuccess);
    Contains("[REDACTED]", result.Text ?? string.Empty);
    Contains("sk-secret", result.Text ?? string.Empty, false);
}

static async Task TestCancellation()
{
    var settings = ValidSettings();
    var main = new Main();
    main.Init(ContextProxy.Create(settings, streamChunks: ["data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n"], waitAfterStream: true));
    using var source = new CancellationTokenSource();
    source.CancelAfter(50);
    var cancelled = false;
    try
    {
        await main.TranslateAsync(new TranslateRequest("hello", LangEnum.English, LangEnum.ChineseSimplified), new TranslateResult(), source.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    Equal(true, cancelled);
}

static Task TestInterfaces()
{
    var main = new Main();
    Equal(true, main is LlmTranslatePluginBase);
    Equal(true, main is IOcrPlugin);
    return Task.CompletedTask;
}

static Task TestPackage()
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var package = Path.Combine(root, ".artifacts", "plugins", "STranslate.Plugin.Bailian.spkg");
    True(File.Exists(package), $"Package not found: {package}");
    using var archive = ZipFile.OpenRead(package);
    var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var required in new[] { "plugin.json", "icon.png", "STranslate.Plugin.Bailian.dll", "STranslate.Plugin.Bailian.deps.json" })
        True(names.Contains(required), $"Package is missing {required}.");
    True(names.All(name => !name.Contains('/')), "Package files must be at the archive root.");
    return Task.CompletedTask;
}

static Settings ValidSettings() => new()
{
    ApiKey = "sk-test",
    AccessMode = BillingMode.PayAsYouGo,
    Model = "qwen3.7-plus",
    Region = "china",
    Stream = true
};

static byte[] PngBytes() => [137, 80, 78, 71, 13, 10, 26, 10, 0];

static string NativeOcrJson(string coordinate) =>
    "{\"output\":{\"choices\":[{\"message\":{\"content\":[{\"text\":\"line\",\"ocr_result\":{\"words_info\":[{\"text\":\"line\"," + coordinate + "}]}}]}}]}}";

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Contains(string value, string actual, bool expected = true)
{
    var found = actual.Contains(value, StringComparison.Ordinal);
    if (found != expected) throw new InvalidOperationException(expected ? $"Missing {value}." : $"Unexpected {value}.");
}

static void Near(float expected, float actual)
{
    if (MathF.Abs(expected - actual) > 0.01f) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

internal class ContextProxy : DispatchProxy
{
    private Settings _settings = null!;
    private IHttpService _httpService = null!;
    public int SaveCount { get; private set; }
    public HttpProxy Http => (HttpProxy)(object)_httpService;

    public static ContextProxy For(IPluginContext context) => (ContextProxy)(object)context;

    public static IPluginContext Create(Settings settings, string? postResponse = null, string[]? streamChunks = null, bool waitAfterStream = false)
    {
        var context = DispatchProxy.Create<IPluginContext, ContextProxy>();
        var proxy = (ContextProxy)(object)context;
        proxy._settings = settings;
        proxy._httpService = HttpProxy.Create(postResponse, streamChunks, waitAfterStream);
        return context;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "SaveSettingStorage")
        {
            SaveCount++;
            return null;
        }
        return targetMethod?.Name switch
        {
            "get_HttpService" => _httpService,
            "LoadSettingStorage" => _settings,
            "GetTranslation" => args?[0]?.ToString() ?? string.Empty,
            _ => DefaultValue(targetMethod?.ReturnType)
        };
    }

    private static object? DefaultValue(Type? type) => type is null || type == typeof(void)
        ? null
        : type.IsValueType ? Activator.CreateInstance(type) : null;
}

internal class HttpProxy : DispatchProxy
{
    private string _postResponse = "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}";
    private string[] _streamChunks = [];
    private bool _waitAfterStream;
    public string? LastUrl { get; private set; }
    public object? LastBody { get; private set; }

    public static IHttpService Create(string? postResponse, string[]? streamChunks, bool waitAfterStream)
    {
        var service = DispatchProxy.Create<IHttpService, HttpProxy>();
        var proxy = (HttpProxy)(object)service;
        if (postResponse is not null) proxy._postResponse = postResponse;
        if (streamChunks is not null) proxy._streamChunks = streamChunks;
        proxy._waitAfterStream = waitAfterStream;
        return service;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "PostAsync" && targetMethod.ReturnType == typeof(Task<string>))
        {
            LastUrl = args?[0]?.ToString();
            LastBody = args?[1];
            return Task.FromResult(_postResponse);
        }
        if (targetMethod?.Name == "StreamPostAsyncEnumerable")
        {
            var token = args?.OfType<CancellationToken>().LastOrDefault() ?? default;
            return Stream(_streamChunks, _waitAfterStream, token);
        }
        throw new NotSupportedException(targetMethod?.Name);
    }

    private static async IAsyncEnumerable<string> Stream(
        IEnumerable<string> chunks,
        bool waitAfterStream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
        if (waitAfterStream) await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
