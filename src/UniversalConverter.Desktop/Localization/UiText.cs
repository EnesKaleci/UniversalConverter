using System.Globalization;
using System.Resources;

namespace UniversalConverter.Desktop;

public sealed class UiText
{
    internal const string English = "en-US";
    internal const string Turkish = "tr-TR";

    internal static ResourceManager Catalog { get; } =
        new("UniversalConverter.Desktop.Localization.Strings", typeof(UiText).Assembly);

    private readonly ResourceManager _resources;

    internal UiText(string languageTag, ResourceManager? resources = null)
    {
        Culture = CultureInfo.GetCultureInfo(languageTag);
        _resources = resources ?? Catalog;
    }

    public CultureInfo Culture { get; }

    public string AppName => Get(nameof(AppName));
    public string Dashboard => Get(nameof(Dashboard));
    public string Convert => Get(nameof(Convert));
    public string Queue => Get(nameof(Queue));
    public string History => Get(nameof(History));
    public string Settings => Get(nameof(Settings));
    public string About => Get(nameof(About));
    public string SimulationBanner => Get(nameof(SimulationBanner));
    public string DashboardHeading => Get(nameof(DashboardHeading));
    public string DashboardIntro => Get(nameof(DashboardIntro));
    public string OpenConvert => Get(nameof(OpenConvert));
    public string DropTitle => Get(nameof(DropTitle));
    public string SupportedImages => Get(nameof(SupportedImages));
    public string ChooseFiles => Get(nameof(ChooseFiles));
    public string PickerTitle => Get(nameof(PickerTitle));
    public string PickerFilter => Get(nameof(PickerFilter));
    public string TargetFormat => Get(nameof(TargetFormat));
    public string QueueSimulation => Get(nameof(QueueSimulation));
    public string ClearFiles => Get(nameof(ClearFiles));
    public string CancelJob => Get(nameof(CancelJob));
    public string HistoryNote => Get(nameof(HistoryNote));
    public string Language => Get(nameof(Language));
    public string EnglishName => Get(nameof(EnglishName));
    public string TurkishName => Get(nameof(TurkishName));
    public string Appearance => Get(nameof(Appearance));
    public string ThemeSystem => Get(nameof(ThemeSystem));
    public string ThemeLight => Get(nameof(ThemeLight));
    public string ThemeDark => Get(nameof(ThemeDark));
    public string SaveTheme => Get(nameof(SaveTheme));
    public string AboutDescription => Get(nameof(AboutDescription));

    public string FilesEmpty => Get(nameof(FilesEmpty));
    public string FileList => Get(nameof(FileList));
    public string RemoveSelected => Get(nameof(RemoveSelected));
    public string CollisionPolicy => Get(nameof(CollisionPolicy));
    public string CollisionRename => Get(nameof(CollisionRename));
    public string CollisionSkip => Get(nameof(CollisionSkip));
    public string OutputFolder => Get(nameof(OutputFolder));
    public string ChooseOutputFolder => Get(nameof(ChooseOutputFolder));
    public string ResetOutputFolder => Get(nameof(ResetOutputFolder));
    public string RefreshPreview => Get(nameof(RefreshPreview));
    public string PreparingFiles => Get(nameof(PreparingFiles));
    public string QueueEmpty => Get(nameof(QueueEmpty));
    public string HistoryEmpty => Get(nameof(HistoryEmpty));

    public string ImageBanner => Get(nameof(ImageBanner));
    public string ImageQuality => Get(nameof(ImageQuality));
    public string JpegWhiteBackground => Get(nameof(JpegWhiteBackground));
    public string ConvertImages => Get(nameof(ConvertImages));
    public string ImageHistory => Get(nameof(ImageHistory));
    public string SimulationHistory => Get(nameof(SimulationHistory));
    public string OpenLocation => Get(nameof(OpenLocation));

    public string MediaMode => Get(nameof(MediaMode));
    public string MediaQuality => Get(nameof(MediaQuality));
    public string MediaLow => Get(nameof(MediaLow));
    public string MediaStandard => Get(nameof(MediaStandard));
    public string MediaHigh => Get(nameof(MediaHigh));
    public string MediaCustom => Get(nameof(MediaCustom));
    public string MediaBitrate => Get(nameof(MediaBitrate));
    public string MediaResolution => Get(nameof(MediaResolution));
    public string MediaFrameRate => Get(nameof(MediaFrameRate));
    public string MediaSource => Get(nameof(MediaSource));
    public string MediaQualityNote => Get(nameof(MediaQualityNote));

    internal string Get(string key) =>
        Read(key, Culture) ?? Read(key, CultureInfo.InvariantCulture) ??
        Read("TextUnavailable", CultureInfo.InvariantCulture) ?? "Text unavailable.";

    internal string Format(string key, params object[] arguments) => string.Format(Culture, Get(key), arguments);

    private string? Read(string key, CultureInfo culture)
    {
        try
        {
            var value = _resources.GetString(key, culture);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or MissingManifestResourceException or MissingSatelliteAssemblyException)
        {
            // An unavailable satellite must leave the embedded English strings usable.
            return null;
        }
    }
}

public sealed class UiLocalization
{
    private readonly CultureInfo _systemCulture;

    public UiLocalization(CultureInfo systemCulture)
    {
        _systemCulture = systemCulture;
        Text = new(ResolveLanguage(null, systemCulture));
    }

    public UiText Text { get; private set; }

    internal static UiLocalization FromSystem() => new(CultureInfo.CurrentUICulture);

    internal UiText Apply(string? preference)
    {
        var language = ResolveLanguage(preference, _systemCulture);
        if (Text.Culture.Name != language)
            Text = new(language);
        return Text;
    }

    internal static string ResolveLanguage(string? preference, CultureInfo systemCulture)
    {
        if (string.Equals(preference, UiText.Turkish, StringComparison.OrdinalIgnoreCase))
            return UiText.Turkish;
        if (string.Equals(preference, UiText.English, StringComparison.OrdinalIgnoreCase))
            return UiText.English;

        return string.Equals(systemCulture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase)
            ? UiText.Turkish : UiText.English;
    }
}
