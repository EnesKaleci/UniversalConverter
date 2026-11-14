namespace UniversalConverter.Domain;

public static class ImageFormats
{
    public const int DefaultQuality = 85;
    public static FileFormat Png { get; } = new("png", FormatCategory.Image);
    public static FileFormat Jpeg { get; } = new("jpg", FormatCategory.Image);
    public static FileFormat JpegLong { get; } = new("jpeg", FormatCategory.Image);
    public static FileFormat WebP { get; } = new("webp", FormatCategory.Image);
    public static IReadOnlyList<FileFormat> All { get; } = Array.AsReadOnly(new[] { Png, Jpeg, WebP });
    public static IReadOnlyList<ConversionCapability> Pairs { get; } = Array.AsReadOnly(
        (from source in All
         from target in All.Append(JpegLong)
         where source != (target == JpegLong ? Jpeg : target)
         select new ConversionCapability(source, target)).ToArray());
}

public enum ConversionFailure { InvalidImage, FormatMismatch, AnimatedImage, ImageTooLarge, SourceAccess, OutputAccess, Encoding, DiskFull }

public sealed class ConversionException(ConversionFailure reason, Exception? inner = null) : IOException(reason.ToString(), inner)
{
    public ConversionFailure Reason { get; } = reason;
}

public sealed record ConversionHistoryEntry(Guid Id, string SourcePath, string Target, string? OutputPath,
    ConversionStatus Status, string? Error, DateTimeOffset FinishedAt, TimeSpan Duration);
