namespace UniversalConverter.Domain;

public enum ConversionStatus { Queued, Running, Completed, Failed, Cancelled, Skipped }
public enum OutputOverwritePolicy { Reject, Replace }

public sealed record ConversionRequest(
    string InputPath, FileFormat Target, string? OutputPath = null,
    OutputOverwritePolicy OverwritePolicy = OutputOverwritePolicy.Reject,
    bool? Simulation = null, OutputCollisionPolicy? CollisionPolicy = null, int Quality = ImageFormats.DefaultQuality);

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
    public bool IsSkipped { get; }

    private ConversionResult(bool isSimulation, string? outputPath, bool isSkipped = false)
    {
        IsSimulation = isSimulation;
        OutputPath = outputPath;
        IsSkipped = isSkipped;
    }

    public static ConversionResult Simulated() => new(true, null);

    public static ConversionResult Skipped() => new(false, null, true);

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
    public bool CanCancel { get; private init; } = true;
    public DateTimeOffset? StartedAt { get; private init; }
    public DateTimeOffset? FinishedAt { get; private init; }

    private ConversionJob(ConversionRequest request)
    {
        Id = Guid.NewGuid();
        Request = request;
        Status = ConversionStatus.Queued;
    }

    public bool IsTerminal => Status is ConversionStatus.Completed or ConversionStatus.Failed or ConversionStatus.Cancelled or ConversionStatus.Skipped;

    public static ConversionJob Create(ConversionRequest request) =>
        new(request ?? throw new ArgumentNullException(nameof(request)));

    public ConversionJob Start() => Status == ConversionStatus.Queued
        ? this with { Status = ConversionStatus.Running, StartedAt = DateTimeOffset.UtcNow }
        : throw InvalidTransition();

    public ConversionJob StopAcceptingCancellation() => Status == ConversionStatus.Running
        ? this with { CanCancel = false }
        : throw InvalidTransition();

    public ConversionJob Report(ConversionProgress progress) => Status == ConversionStatus.Running
        ? this with { Percent = Math.Max(Percent, progress.Percent) }
        : throw InvalidTransition();

    public ConversionJob Complete(ConversionResult result) => Status == ConversionStatus.Running
        ? this with
        {
            Status = (result ?? throw new ArgumentNullException(nameof(result))).IsSkipped ? ConversionStatus.Skipped : ConversionStatus.Completed,
            Percent = 100, Result = result, FinishedAt = DateTimeOffset.UtcNow
        }
        : throw InvalidTransition();

    public ConversionJob Fail(string error) => Status == ConversionStatus.Running
        ? this with { Status = ConversionStatus.Failed, Error = error, FinishedAt = DateTimeOffset.UtcNow }
        : throw InvalidTransition();

    public ConversionJob Cancel() => !IsTerminal
        ? this with { Status = ConversionStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow }
        : throw InvalidTransition();

    private InvalidOperationException InvalidTransition() => new($"Invalid transition from {Status}.");
}
