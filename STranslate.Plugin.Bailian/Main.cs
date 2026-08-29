using System.Text;
using System.Windows.Controls;
using STranslate.Plugin;

namespace STranslate.Plugin.Bailian;

public sealed class Main : LlmTranslatePluginBase, IOcrPlugin
{
    private IPluginContext Context { get; set; } = null!;
    private Settings Settings { get; set; } = new();
    private Control? _settingsView;

    public IEnumerable<LangEnum> SupportedLanguages => Enum.GetValues<LangEnum>();

    public override void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();
        if (Settings.Prompts.Count == 0) Settings.Prompts = Settings.DefaultPrompts();
        Prompts.Clear();
        foreach (var prompt in Settings.Prompts) Prompts.Add(prompt);
    }

    public override Control GetSettingUI() =>
        _settingsView ??= new SettingsView(Context, Settings, SaveSettings, TestConnectionAsync, EditPrompts);

    public override string? GetSourceLanguage(LangEnum langEnum) => LanguageMap.TranslateName(langEnum);

    public override string? GetTargetLanguage(LangEnum langEnum) => LanguageMap.TranslateName(langEnum);

    public override async Task TranslateAsync(
        TranslateRequest request,
        TranslateResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BailianConfig.Validate(Settings, false);
            var source = GetSourceLanguage(request.SourceLang)
                ?? throw new InvalidOperationException($"Unsupported source language: {request.SourceLang}.");
            var target = GetTargetLanguage(request.TargetLang)
                ?? throw new InvalidOperationException($"Unsupported target language: {request.TargetLang}.");
            if (request.Text.Length == 0)
            {
                result.Success(string.Empty);
                return;
            }

            var body = BailianProtocol.TranslationRequest(Settings, request.Text, source, target, SelectedPrompt);
            var endpoint = BailianConfig.ChatEndpoint(Settings);
            var options = BailianProtocol.RequestOptions(Settings);
            if (body["stream"] is true)
            {
                var text = new StringBuilder();
                var finishedByLength = false;
                await foreach (var line in Context.HttpService.StreamPostAsyncEnumerable(endpoint, body, options, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    text.Append(BailianProtocol.ParseSseChunk(line, out var truncated));
                    finishedByLength |= truncated;
                    if (text.Length > 0) result.Success(text.ToString());
                }
                if (finishedByLength)
                    throw new InvalidOperationException("Model Studio output was truncated; increase Max tokens or reduce the input.");
                if (text.Length == 0) throw new InvalidOperationException("Model Studio stream did not include result text.");
                result.Success(text.ToString());
            }
            else
            {
                var response = await Context.HttpService.PostAsync(endpoint, body, options, cancellationToken);
                result.Success(BailianProtocol.ParseCompletion(response));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result.Fail(BailianProtocol.Redact(exception.Message, Settings.ApiKey));
        }
    }

    public bool SupportBoxPoints() => BailianConfig.IsNativeCoordinateOcr(Settings);

    public string? GetLanguage(LangEnum langEnum) => LanguageMap.TranslateName(langEnum);

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        try
        {
            BailianConfig.Validate(Settings, true);
            var options = BailianProtocol.RequestOptions(Settings);
            if (BailianConfig.IsNativeCoordinateOcr(Settings))
            {
                var response = await Context.HttpService.PostAsync(
                    BailianConfig.NativeOcrEndpoint(Settings),
                    BailianProtocol.NativeOcrRequest(Settings, request.ImageData),
                    options,
                    cancellationToken);
                return BailianProtocol.ParseNativeOcr(response);
            }

            var body = BailianProtocol.GenericOcrRequest(Settings, request.ImageData, LanguageMap.OcrName(request.Language));
            var genericResponse = await Context.HttpService.PostAsync(
                BailianConfig.ChatEndpoint(Settings),
                body,
                options,
                cancellationToken);
            return BailianProtocol.TextOcrResult(BailianProtocol.ParseCompletion(genericResponse));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var result = new OcrResult();
            result.Fail(BailianProtocol.Redact(exception.Message, Settings.ApiKey));
            return result;
        }
    }

    public override void Dispose()
    {
    }

    internal void SaveSettings()
    {
        Settings.Prompts = [.. Prompts.Select(prompt => prompt.Clone())];
        Context.SaveSettingStorage<Settings>();
    }

    internal async Task<string> TestConnectionAsync()
    {
        SaveSettings();
        try
        {
            var options = BailianProtocol.RequestOptions(Settings);
            if (BailianConfig.IsOcrOnly(Settings))
            {
                BailianConfig.Validate(Settings, true);
                var response = await Context.HttpService.PostAsync(
                    BailianConfig.NativeOcrEndpoint(Settings),
                    BailianProtocol.NativeOcrRequest(Settings, TestImage),
                    options,
                    CancellationToken.None);
                _ = BailianProtocol.ParseNativeOcr(response);
            }
            else
            {
                BailianConfig.Validate(Settings, false);
                var body = BailianProtocol.TranslationRequest(
                    Settings,
                    "hello",
                    "English",
                    "Simplified Chinese");
                body["stream"] = false;
                var response = await Context.HttpService.PostAsync(
                    BailianConfig.ChatEndpoint(Settings),
                    body,
                    options,
                    CancellationToken.None);
                _ = BailianProtocol.ParseCompletion(response);
            }

            return $"连接成功：{BailianConfig.BillingModeLabel(Settings.AccessMode)} · {Settings.Model.Trim()}";
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(BailianProtocol.Redact(exception.Message, Settings.ApiKey));
        }
    }

    private void EditPrompts()
    {
        Context.GetPromptEditWindow(Prompts, ["system", "user"]).ShowDialog();
        SaveSettings();
    }

    private static readonly byte[] TestImage = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlPpFcAAAAASUVORK5CYII=");
}
