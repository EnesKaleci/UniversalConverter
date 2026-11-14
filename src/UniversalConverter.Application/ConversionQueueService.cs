using System.Threading.Channels;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Application;

public sealed class ConversionQueueService : IAsyncDisposable
{
    private sealed class Entry(ConversionJob job, ConversionPlan plan)
    {
        public ConversionJob Job { get; set; } = job;
        public ConversionPlan Plan { get; } = plan;
        public CancellationTokenSource Cancellation { get; } = new();
        public bool Committing { get; set; }
        public Task? CancellationCallbacks { get; set; }
        public TaskCompletionSource<ConversionJob> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProgressCallback(Action<ConversionProgress> report) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => report(value);
    }

    private readonly object _gate = new();
    private readonly ConversionCoordinator _coordinator;
    private readonly Channel<Entry> _pending;
    private readonly Dictionary<Guid, Entry> _jobs = [];
    private readonly Task _workers;
    private readonly int _historyCapacity;
    private bool _stopping;
    private readonly IConversionHistory? _history;
    private readonly Action<Exception>? _historyError;
    private Task _historyWrites = Task.CompletedTask;

    public ConversionQueueService(ConversionCoordinator coordinator, int concurrency = 2, int capacity = 128, int historyCapacity = 256,
        IConversionHistory? history = null, Action<Exception>? historyError = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyCapacity);
        _coordinator = coordinator;
        _historyCapacity = historyCapacity;
        _history = history;
        _historyError = historyError;
        _pending = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
        _workers = Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(WorkerAsync)));
    }

    public (Guid Id, Task<ConversionJob> Completion) Enqueue(ConversionRequest request)
    {
        var plan = _coordinator.Prepare(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var entry = new Entry(ConversionJob.Create(plan.Request), plan);
            if (!_pending.Writer.TryWrite(entry))
            {
                entry.Cancellation.Dispose();
                throw new InvalidOperationException("The queue is full.");
            }

            _jobs.Add(entry.Job.Id, entry);
            return (entry.Job.Id, entry.Completion.Task);
        }
    }

    public IReadOnlyList<ConversionJob> Snapshot()
    {
        lock (_gate)
            return _jobs.Values.Reverse().Select(entry => entry.Job).ToArray();
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
            return _jobs.TryGetValue(id, out var entry) && RequestCancellation(entry);
    }

    private bool RequestCancellation(Entry entry)
    {
        if (entry.Job.IsTerminal || entry.Committing || entry.CancellationCallbacks is not null)
            return false;

        if (entry.Job.Status == ConversionStatus.Queued)
            Finish(entry, entry.Job.Cancel());
        else
            entry.CancellationCallbacks = entry.Cancellation.CancelAsync();
        return true;
    }

    private async Task WorkerAsync()
    {
        await foreach (var entry in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (entry.Job.IsTerminal)
                {
                    entry.Cancellation.Dispose();
                    continue;
                }
                entry.Job = entry.Job.Start();
            }

            ConversionResult? result = null;
            string? error = null;
            try
            {
                var progress = new ProgressCallback(value =>
                {
                    lock (_gate)
                    {
                        if (entry.Job.Status == ConversionStatus.Running && entry.CancellationCallbacks is null)
                            entry.Job = entry.Job.Report(value);
                    }
                });
                result = await _coordinator.ExecuteAsync(entry.Plan, progress, entry.Cancellation.Token, () => BeginCommit(entry)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                error = exception switch
                {
                    ConversionException failure => failure.Reason.ToString(),
                    _ when entry.Plan.Engine is IOutputCommitEngine => ConversionFailure.Encoding.ToString(),
                    IOException or NotSupportedException => exception.Message,
                    _ => "The conversion engine failed."
                };
            }

            Task? callbacks;
            lock (_gate)
            {
                callbacks = entry.CancellationCallbacks;
                var terminal = callbacks is not null ? entry.Job.Cancel()
                    : result is not null ? entry.Job.Complete(result)
                    : entry.Job.Fail(error ?? "The conversion engine failed.");
                Finish(entry, terminal);
            }
            try
            {
                if (callbacks is not null)
                    await callbacks.ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // An adapter's cancellation callback must not take a queue worker down.
            }
            finally
            {
                entry.Cancellation.Dispose();
            }
        }
    }

    private void BeginCommit(Entry entry)
    {
        lock (_gate)
        {
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            entry.Committing = true;
            entry.Job = entry.Job.BeginCommit();
        }
    }

    private void Finish(Entry entry, ConversionJob terminal)
    {
        entry.Job = terminal;
        if (_history is not null && !entry.Plan.Engine.IsSimulation)
        {
            var finished = terminal.FinishedAt!.Value;
            var record = new ConversionHistoryEntry(terminal.Id, terminal.Request.InputPath, terminal.Request.Target.Extension,
                terminal.Result?.OutputPath, terminal.Status, terminal.Error, finished, finished - (terminal.StartedAt ?? finished));
            var previous = _historyWrites;
            _historyWrites = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try { await _history.StoreAsync(record).ConfigureAwait(false); }
                catch (Exception error) { _historyError?.Invoke(error); }
            });
        }
        entry.Completion.TrySetResult(terminal);
        var expired = _jobs.Values.Reverse().Where(item => item.Job.IsTerminal).Skip(_historyCapacity).ToArray();
        foreach (var item in expired)
            _jobs.Remove(item.Job.Id);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                _pending.Writer.TryComplete();
                foreach (var entry in _jobs.Values.ToArray())
                    RequestCancellation(entry);
            }
        }
        await _workers.ConfigureAwait(false);
        await _historyWrites.ConfigureAwait(false);
    }
}
