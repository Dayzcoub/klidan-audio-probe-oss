using System.Globalization;
using System.Resources;

namespace KlidanAudioProbe.Gui.Core;

public static class Localization
{
    private static readonly ResourceManager Resources = new("KlidanAudioProbe.Gui.Core.UiResources", typeof(Localization).Assembly);
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlidanAudioProbe", "language.txt");

    public static IReadOnlyList<StringSelectionOption> Languages { get; } =
    [new("ru-RU", "Русский"), new("en-US", "English")];

    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentUICulture;

    public static void Initialize()
    {
        var saved = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : null;
        var requested = saved is "ru-RU" or "en-US" ? saved : CultureInfo.InstalledUICulture.Name;
        SetLanguage(requested.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US", false);
    }

    public static void SetLanguage(string language, bool persist = true)
    {
        var name = language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US";
        CurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CurrentCulture;
        if (!persist) return;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, name);
    }

    public static string Get(string key) => Resources.GetString(key, CurrentCulture) ?? Resources.GetString(key, CultureInfo.GetCultureInfo("en-US")) ?? key;
    public static string Get(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? Resources.GetString(key, CultureInfo.GetCultureInfo("en-US")) ?? key;
}
