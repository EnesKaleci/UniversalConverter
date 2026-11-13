# Conversion pipeline

1. The picker or drop zone adds supported local paths to a batch.
2. The planner normalizes the absolute input path, checks file presence and detects the format by extension.
3. The registry selects the first engine with the requested source and target pair. Registration order provides deterministic priority.
4. The bounded channel dispatches queued jobs to two workers. The default limit is 128 pending entries and 256 retained terminal snapshots.
5. The coordinator calls the selected adapter with a cancellation token and progress reporter. The simulation engine emits progress and returns a simulation marker.
6. The queue records a terminal state and completes the ticket. Queue snapshots feed the UI.

Queued jobs can start or cancel. Running jobs can complete, fail or cancel. Terminal jobs reject further transitions. A cancelled queued entry occupies its channel slot until a worker reads it. Shutdown stops accepting jobs, cancels pending and active work, then waits for the workers.

The M0 format detector uses extensions only. A real adapter must verify content as needed and publish through an atomic output policy.
