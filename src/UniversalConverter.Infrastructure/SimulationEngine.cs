using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Infrastructure;

public sealed class SimulationEngine : IConverterEngine
{
    public static FileFormat Png => ImageFormats.Png;
    public static FileFormat Jpeg => ImageFormats.Jpeg;
    public static FileFormat WebP => ImageFormats.WebP;
    public static IReadOnlyList<FileFormat> Formats => ImageFormats.All;

    public string Id => "simulation";
    public bool IsSimulation => true;
    public IReadOnlyList<ConversionCapability> Capabilities => ImageFormats.Pairs;

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
