namespace UniversalConverter.Domain;

public enum ConversionStatus { Queued, Running, Completed, Failed, Cancelled }
public enum OutputOverwritePolicy { Reject, Replace }

public sealed record ConversionRequest(
    string InputPath, FileFormat Target, string? OutputPath = null,
    OutputOverwritePolicy OverwritePolicy = OutputOverwritePolicy.Reject);

public sealed record ConversionProgress
{
    public int Percent { get; }

    public ConversionProgress(int percent)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
        Percent = percent;
    }
}

public sealed record ConversionResult
{
    public bool IsSimulation { get; }
    public string? OutputPath { get; }

    private ConversionResult(bool isSimulation, string? outputPath)
    {
        IsSimulation = isSimulation;
        OutputPath = outputPath;
    }

    public static ConversionResult Simulated() => new(true, null);

    public static ConversionResult CreatedFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(false, path);
    }
}

public sealed record ConversionJob
{
    public Guid Id { get; }
    public ConversionRequest Request { get; }
    public ConversionStatus Status { get; private init; }
    public int Percent { get; private init; }
    public ConversionResult? Result { get; private init; }
    public string? Error { get; private init; }

    private ConversionJob(ConversionRequest request)
    {
        Id = Guid.NewGuid();
        Request = request;
        Status = ConversionStatus.Queued;
    }

    public bool IsTerminal => Status is ConversionStatus.Completed or ConversionStatus.Failed or ConversionStatus.Cancelled;

    public static ConversionJob Create(ConversionRequest request) =>
        new(request ?? throw new ArgumentNullException(nameof(request)));

    public ConversionJob Start() => Status == ConversionStatus.Queued
        ? this with { Status = ConversionStatus.Running }
        : throw InvalidTransition();

    public ConversionJob Report(ConversionProgress progress) => Status == ConversionStatus.Running
        ? this with { Percent = Math.Max(Percent, progress.Percent) }
        : throw InvalidTransition();

    public ConversionJob Complete(ConversionResult result) => Status == ConversionStatus.Running
        ? this with { Status = ConversionStatus.Completed, Percent = 100, Result = result ?? throw new ArgumentNullException(nameof(result)) }
        : throw InvalidTransition();

    public ConversionJob Fail(string error) => Status == ConversionStatus.Running
        ? this with { Status = ConversionStatus.Failed, Error = error }
        : throw InvalidTransition();

    public ConversionJob Cancel() => !IsTerminal
        ? this with { Status = ConversionStatus.Cancelled }
        : throw InvalidTransition();

    private InvalidOperationException InvalidTransition() => new($"Invalid transition from {Status}.");
}
