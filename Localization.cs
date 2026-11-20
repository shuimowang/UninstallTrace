using System.Globalization;
using System.IO;
using System.Security;
using System.Windows.Markup;

namespace DeepUninstaller;

public enum AppLanguage { System, SimplifiedChinese, English }

public static class Localization
{
    private static readonly AppLanguage SystemLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"
        ? AppLanguage.SimplifiedChinese : AppLanguage.English;
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UninstallTrace", "language.txt");

    public static AppLanguage Preference { get; private set; } = ReadPreference();
    public static AppLanguage Current => Preference == AppLanguage.System ? SystemLanguage : Preference;
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Current == AppLanguage.SimplifiedChinese ? "zh-CN" : "en-US");

    public static string Text(string chinese, string english) => Current == AppLanguage.SimplifiedChinese ? chinese : english;

    internal static void SavePreference(AppLanguage language)
    {
        if (!Enum.IsDefined(language))
            throw new ArgumentOutOfRangeException(nameof(language));
        if (language == Preference)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = $"{SettingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, language.ToString());
            File.Move(temporary, SettingsPath, overwrite: true);
            Preference = language;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static AppLanguage ReadPreference()
    {
        try
        {
            return Enum.TryParse<AppLanguage>(File.ReadAllText(SettingsPath).Trim(), out var language)
                && Enum.IsDefined(language) ? language : AppLanguage.System;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return AppLanguage.System;
        }
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class UiTextExtension(string chinese, string english) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Localization.Text(chinese, english);
}
