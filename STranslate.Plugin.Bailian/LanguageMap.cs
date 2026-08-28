using STranslate.Plugin;

namespace STranslate.Plugin.Bailian;

internal static class LanguageMap
{
    public static string? TranslateName(LangEnum language) => language switch
    {
        LangEnum.Auto => "Auto detect",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal => "Portuguese (Portugal)",
        LangEnum.PortugueseBrazil => "Portuguese (Brazil)",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic => "Mongolian",
        LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmal",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        _ => null
    };

    public static string QwenMtName(LangEnum language, bool source)
    {
        var value = TranslateName(language);
        if (source && language == LangEnum.Auto) return "auto";
        if (!source && language == LangEnum.Auto) return "English";
        return value ?? throw new InvalidOperationException($"Unsupported language: {language}.");
    }

    public static string OcrName(LangEnum language) =>
        language == LangEnum.Auto ? "Automatically detected" : TranslateName(language) ?? "Automatically detected";
}
