# ADR 002 · Layer boundaries

Accepted for M0. Keep domain types dependency-free, define conversion ports in Contracts and orchestrate jobs in Application. Put concrete adapters in Infrastructure and wire them at the desktop composition root.

Use bounded asynchronous workers, cooperative cancellation and immutable snapshots. Keep simulated completion distinguishable in both the API and UI. Defer dynamic plugin loading and persistent conversion history until a feature requires them.
