using System.Globalization;

namespace vRemoter.Core.Localization;

public enum AppLanguage
{
    System,
    SimplifiedChinese,
    English
}

public static class AppLanguageExtensions
{
    public static string ToStorageValue(this AppLanguage language) => language switch
    {
        AppLanguage.SimplifiedChinese => "zh-Hans",
        AppLanguage.English => "en",
        _ => "system"
    };

    public static AppLanguage ParseStorageValue(string? raw) => raw switch
    {
        "zh-Hans" => AppLanguage.SimplifiedChinese,
        "en" => AppLanguage.English,
        _ => AppLanguage.System
    };

    public static bool UsesEnglish(this AppLanguage language) => language switch
    {
        AppLanguage.English => true,
        AppLanguage.SimplifiedChinese => false,
        _ => !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase)
    };
}

public static class L10n
{
    private static Func<AppLanguage> _languageProvider = () => AppLanguage.System;

    public static AppLanguage CurrentLanguage => _languageProvider();

    public static void Configure(Func<AppLanguage> languageProvider)
    {
        _languageProvider = languageProvider ?? throw new ArgumentNullException(nameof(languageProvider));
    }

    public static string Text(string simplifiedChinese, string english) =>
        _languageProvider().UsesEnglish() ? english : simplifiedChinese;
}

