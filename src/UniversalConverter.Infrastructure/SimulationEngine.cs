using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Infrastructure;

public sealed class SimulationEngine : IConverterEngine
{
    public static FileFormat Png { get; } = new("png", FormatCategory.Image);
    public static FileFormat Jpeg { get; } = new("jpg", FormatCategory.Image);
    public static FileFormat WebP { get; } = new("webp", FormatCategory.Image);
    public static IReadOnlyList<FileFormat> Formats { get; } = Array.AsReadOnly(new[] { Png, Jpeg, WebP });

    public string Id => "simulation";
    public bool IsSimulation => true;
    public IReadOnlyList<ConversionCapability> Capabilities { get; } = Array.AsReadOnly(new[]
    {
        new ConversionCapability(Png, Jpeg), new ConversionCapability(Png, WebP),
        new ConversionCapability(Jpeg, Png), new ConversionCapability(Jpeg, WebP),
        new ConversionCapability(WebP, Png), new ConversionCapability(WebP, Jpeg)
    });

    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken cancellationToken)
    {
        for (var percent = 10; percent <= 100; percent += 10)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(160), cancellationToken).ConfigureAwait(false);
            progress.Report(new ConversionProgress(percent));
        }
        return ConversionResult.Simulated();
    }
}
