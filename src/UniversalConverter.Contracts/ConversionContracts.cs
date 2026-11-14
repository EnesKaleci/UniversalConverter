using UniversalConverter.Domain;

namespace UniversalConverter.Contracts;

public interface IConverterEngine
{
    string Id { get; }
    bool IsSimulation { get; }
    IReadOnlyList<ConversionCapability> Capabilities { get; }
    Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken cancellationToken);
}

public interface IOutputCommitEngine : IConverterEngine
{
    Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
        CancellationToken cancellationToken, Action beginCommit);
}

public interface IConversionHistory
{
    Task StoreAsync(ConversionHistoryEntry entry);
    Task<IReadOnlyList<ConversionHistoryEntry>> LoadAsync();
}

public interface IConversionEngineRegistry
{
    IReadOnlyList<IConverterEngine> Engines { get; }
}

public interface IFormatDetector
{
    FileFormat? Detect(string path);
}

public sealed record ConversionPlan(IConverterEngine Engine, FileFormat Source, ConversionRequest Request);

public interface IConversionPlanner
{
    ConversionPlan Plan(ConversionRequest request);
}

public enum ThemePreference { System, Light, Dark }

public interface ISettingsStore
{
    Task<ThemePreference> LoadThemeAsync(CancellationToken cancellationToken = default);
    Task SaveThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default);
    Task<string?> LoadLanguageAsync(CancellationToken cancellationToken = default);
    Task SaveLanguageAsync(string languageTag, CancellationToken cancellationToken = default);
}
