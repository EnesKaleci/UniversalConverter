using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;
using Xunit;

namespace UniversalConverter.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private static readonly FileFormat Source = new("png", FormatCategory.Image);
    private static readonly FileFormat Target = new("jpg", FormatCategory.Image);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private readonly string _directory = Directory.CreateTempSubdirectory("converter-test-").FullName;

    [Theory]
    [InlineData(".PNG")]
    [InlineData(" png ")]
    public void NormalizesExtension(string extension) => Assert.Equal(Source, new FileFormat(extension, FormatCategory.Image));

    [Theory]
    [InlineData("")]
    [InlineData("../png")]
    [InlineData("tar.gz")]
    [InlineData("a/b")]
    public void RejectsInvalidExtensions(string extension) =>
        Assert.ThrowsAny<ArgumentException>(() => new FileFormat(extension, FormatCategory.Image));

    [Fact]
    public void ProgressHasBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConversionProgress(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConversionProgress(101));
        Assert.Equal(100, new ConversionProgress(100).Percent);
    }

    [Fact]
    public void StatusTransitionsPreserveInvariants()
    {
        var queued = ConversionJob.Create(new("test.png", Target));
        Assert.Throws<InvalidOperationException>(() => queued.Complete(ConversionResult.Simulated()));
        Assert.Equal(ConversionStatus.Cancelled, queued.Cancel().Status);
        var running = queued.Start().Report(new(50)).Report(new(20));
        Assert.Equal(50, running.Percent);
        Assert.Throws<InvalidOperationException>(() => running.Start());
        Assert.Equal(ConversionStatus.Failed, running.Fail("failure").Status);
        var complete = running.Complete(ConversionResult.Simulated());
        Assert.Equal(ConversionStatus.Completed, complete.Status);
        Assert.Equal(100, complete.Percent);
        Assert.True(complete.Result!.IsSimulation);
        Assert.Null(complete.Result.OutputPath);
        Assert.Throws<InvalidOperationException>(() => complete.Cancel());
    }

    [Fact]
    public void PlanMatchesPairAndRejectsSimulationOutput()
    {
        var engine = new StubEngine((_, _) => Task.FromResult(ConversionResult.Simulated()));
        var planner = Planner(engine);
        var request = CreateRequest();
        Assert.Same(engine, planner.Plan(request).Engine);
        Assert.Throws<NotSupportedException>(() => planner.Plan(request with { Target = Source }));
        Assert.Throws<ArgumentException>(() => planner.Plan(request with { InputPath = "relative.png" }));
        Assert.Throws<ArgumentException>(() => planner.Plan(request with { OutputPath = Path.Combine(_directory, "out.jpg") }));
    }

    [Fact]
    public void RealPlanRejectsOverwriteByDefault()
    {
        var engine = new StubEngine((_, _) => throw new InvalidOperationException(), false);
        var planner = Planner(engine);
        var output = Path.Combine(_directory, "existing.jpg");
        File.WriteAllText(output, "original");
        var request = CreateRequest() with { OutputPath = output };
        Assert.Throws<IOException>(() => planner.Plan(request));
        var plan = planner.Plan(request with { OverwritePolicy = OutputOverwritePolicy.Replace });
        Assert.Equal(output, plan.Request.OutputPath);
        Assert.Equal("original", File.ReadAllText(output));
    }

    [Fact]
    public async Task QueueCapsConcurrencyAndCancelsPendingJobs()
    {
        var started = new SemaphoreSlim(0);
        var release = Signal();
        var engine = new StubEngine(async (_, token) =>
        {
            started.Release();
            await release.Task.WaitAsync(token);
            return ConversionResult.Simulated();
        });
        await using var queue = CreateQueue(engine, concurrency: 2, capacity: 1);
        var first = queue.Enqueue(CreateRequest());
        Assert.True(await started.WaitAsync(Deadline));
        var second = queue.Enqueue(CreateRequest());
        Assert.True(await started.WaitAsync(Deadline));
        var third = queue.Enqueue(CreateRequest());
        Assert.Equal(ConversionStatus.Queued, Assert.Single(queue.Snapshot(), job => job.Id == third.Id).Status);
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(CreateRequest()));
        Assert.True(queue.Cancel(third.Id));
        Assert.Equal(ConversionStatus.Cancelled, (await third.Completion.WaitAsync(Deadline)).Status);
        release.SetResult();
        Assert.Equal(ConversionStatus.Completed, (await first.Completion.WaitAsync(Deadline)).Status);
        Assert.Equal(ConversionStatus.Completed, (await second.Completion.WaitAsync(Deadline)).Status);
        Assert.Equal(0, started.CurrentCount);
    }

    [Fact]
    public async Task RunningJobReportsProgressAndCancels()
    {
        var reported = Signal();
        var engine = new StubEngine(async (progress, token) =>
        {
            progress.Report(new(42));
            reported.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ConversionResult.Simulated();
        });
        await using var queue = CreateQueue(engine);
        var ticket = queue.Enqueue(CreateRequest());
        await reported.Task.WaitAsync(Deadline);
        Assert.Equal(42, Assert.Single(queue.Snapshot()).Percent);
        Assert.True(queue.Cancel(ticket.Id));
        Assert.Equal(ConversionStatus.Cancelled, (await ticket.Completion.WaitAsync(Deadline)).Status);
    }

    [Fact]
    public async Task FailureDoesNotStopWorker()
    {
        var attempts = 0;
        var engine = new StubEngine((_, _) => Interlocked.Increment(ref attempts) == 1
            ? Task.FromException<ConversionResult>(new InvalidOperationException("sensitive detail"))
            : Task.FromResult(ConversionResult.Simulated()));
        await using var queue = CreateQueue(engine);
        var failed = await queue.Enqueue(CreateRequest()).Completion.WaitAsync(Deadline);
        Assert.Equal(ConversionStatus.Failed, failed.Status);
        Assert.Equal("The conversion engine failed.", failed.Error);
        var succeeded = await queue.Enqueue(CreateRequest()).Completion.WaitAsync(Deadline);
        Assert.Equal(ConversionStatus.Completed, succeeded.Status);
    }

    [Fact]
    public async Task ShutdownCancelsWorkAndRejectsNewJobs()
    {
        var started = Signal();
        var engine = new StubEngine(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ConversionResult.Simulated();
        });
        await using var queue = CreateQueue(engine);
        var ticket = queue.Enqueue(CreateRequest());
        await started.Task.WaitAsync(Deadline);
        await queue.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(ConversionStatus.Cancelled, (await ticket.Completion).Status);
        Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(CreateRequest()));
    }

    private ConversionRequest CreateRequest()
    {
        var path = Path.Combine(_directory, Guid.NewGuid() + ".png");
        File.WriteAllText(path, "fixture");
        return new(path, Target);
    }

    private static ConversionPlanner Planner(IConverterEngine engine) =>
        new(new ExtensionFormatDetector([Source]), new ConversionEngineRegistry([engine]));

    private static ConversionQueueService CreateQueue(IConverterEngine engine, int concurrency = 1, int capacity = 4) =>
        new(new ConversionCoordinator(Planner(engine)), concurrency, capacity);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class StubEngine(Func<IProgress<ConversionProgress>, CancellationToken, Task<ConversionResult>> convert, bool simulation = true) : IConverterEngine
    {
        public string Id => "stub";
        public bool IsSimulation => simulation;
        public IReadOnlyList<ConversionCapability> Capabilities { get; } = [new(Source, Target)];
        public Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress, CancellationToken token) =>
            convert(progress, token);
    }
}
