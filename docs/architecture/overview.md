# Architecture

| Project | Direct dependencies | Responsibility |
| --- | --- | --- |
| Domain | None | File formats, capabilities, conversion state, results |
| Contracts | Domain | Adapter, registry, detection, planning and settings interfaces |
| Application | Domain, Contracts | Validation, planning, coordination and bounded queue |
| Infrastructure | Domain, Contracts, Application | Simulation, SQLite, platform, temporary workspace and process primitives |
| Desktop | All layers | Avalonia and MVVM, dependency injection, host and Serilog composition |

Application and its dependencies are package-free. The desktop project composes the known adapters explicitly. Workers run independently of the UI dispatcher, which renders immutable snapshots on a timer. User settings and logs live in per-user storage. Job history is session-only.

Windows x64 is the M0 publication target. Other operating systems require platform-specific packaging and acceptance.
