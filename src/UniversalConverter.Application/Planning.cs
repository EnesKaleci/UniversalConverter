using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Application;

public sealed class ConversionEngineRegistry : IConversionEngineRegistry
{
    public IReadOnlyList<IConverterEngine> Engines { get; }

    public ConversionEngineRegistry(IEnumerable<IConverterEngine> engines)
    {
        var registered = engines.ToArray();
        if (registered.Any(engine => string.IsNullOrWhiteSpace(engine.Id)) ||
            registered.Select(engine => engine.Id).Distinct(StringComparer.Ordinal).Count() != registered.Length)
            throw new ArgumentException("Engine identifiers must be nonempty and unique.", nameof(engines));

        Engines = Array.AsReadOnly(registered);
    }
}

public sealed class ExtensionFormatDetector : IFormatDetector
{
    private readonly Dictionary<string, FileFormat> _formats;

    public ExtensionFormatDetector(IEnumerable<FileFormat> formats)
    {
        _formats = formats.ToDictionary(format => format.Extension, StringComparer.OrdinalIgnoreCase);
        if (_formats.TryGetValue("jpg", out var jpeg))
            _formats["jpeg"] = jpeg;
    }

    public FileFormat? Detect(string path) =>
        _formats.GetValueOrDefault(Path.GetExtension(path).TrimStart('.'));
}

public sealed class ConversionPlanner(IFormatDetector detector, IConversionEngineRegistry registry) : IConversionPlanner
{
    public ConversionPlan Plan(ConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        var input = PathSafety.FullPath(request.InputPath);
        if (!File.Exists(input))
            throw new FileNotFoundException("The input file could not be found.");
        var source = detector.Detect(input) ?? throw new NotSupportedException("The input format is not supported.");
        var engine = registry.Engines.FirstOrDefault(candidate =>
            (request.Simulation is null || candidate.IsSimulation == request.Simulation) &&
            candidate.Capabilities.Any(capability => capability.Matches(source, request.Target)))
            ?? throw new NotSupportedException("No engine supports this format pair.");

        var output = engine.IsSimulation
            ? request.OutputPath is null ? null : throw new ArgumentException("A simulation does not accept an output path.")
            : request.CollisionPolicy is not null
                ? PathSafety.ValidateImageOutput(input, request)
                : PathSafety.ValidateOutput(input, request.OutputPath, request.Target, request.OverwritePolicy);

        return new(engine, source, request with { InputPath = input, OutputPath = output });
    }
}

internal static class PathSafety
{
    public static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute file path is required.");
        if (OperatingSystem.IsWindows() &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
             path[(Path.GetPathRoot(path)?.Length ?? 0)..].Contains(':')))
            throw new ArgumentException("Windows device paths and alternate streams are unsupported.");
        return Path.GetFullPath(path);
    }

    public static string ValidateImageOutput(string input, ConversionRequest request)
    {
        if (request.CollisionPolicy is not { } policy || !Enum.IsDefined(policy) ||
            request.OverwritePolicy != OutputOverwritePolicy.Reject || request.Quality is < 1 or > 100)
            throw new ArgumentException("Invalid image output options.");
        var path = FullPath(request.OutputPath);
        if (string.Equals(input, path, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(path).Equals("." + request.Target.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source and output names are incompatible.");
        return path;
    }

    public static string ValidateOutput(string input, string? outputPath, FileFormat target, OutputOverwritePolicy policy)
    {
        var output = FullPath(outputPath);
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Input and output must differ.");
        if (!Path.GetExtension(output).Equals("." + target.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output extension must match the target format.");
        if (!Directory.Exists(Path.GetDirectoryName(output)) || Directory.Exists(output))
            throw new IOException("Select a file in an existing output directory.");
        if (new FileInfo(output).LinkTarget is not null)
            throw new IOException("The output cannot be a symbolic link.");
        if (policy == OutputOverwritePolicy.Reject && File.Exists(output))
            throw new IOException("The output file already exists.");
        return output;
    }
}
