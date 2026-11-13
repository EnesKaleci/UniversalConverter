namespace UniversalConverter.Domain;

public enum FormatCategory { Video, Audio, Image, Pdf, Office, Archive, Data, Ebook, Other }

public sealed record FileFormat
{
    public string Extension { get; }
    public FormatCategory Category { get; }

    public FileFormat(string extension, FormatCategory category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized.StartsWith('.'))
            normalized = normalized[1..];

        if (normalized.Length is < 1 or > 16 || !normalized.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Expected a single alphanumeric file extension.", nameof(extension));
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category));

        Extension = normalized;
        Category = category;
    }
}

public sealed record ConversionCapability
{
    public FileFormat Source { get; }
    public FileFormat Target { get; }

    public ConversionCapability(FileFormat source, FileFormat target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public bool Matches(FileFormat source, FileFormat target) => Source == source && Target == target;
}
