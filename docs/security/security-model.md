# Security model

Treat selected paths, file contents and tool output as untrusted. M0 simulates jobs and never opens file contents or invokes an engine executable. The queue and captured process streams are bounded. Logs use type-level failures and omit input paths.

- Planning requires absolute paths and existing input files. On Windows it rejects device path prefixes and alternate streams. Output validation requires a matching extension and existing parent, rejects obvious input collisions and output symlinks, and defaults to rejecting overwrite.
- Output checks are advisory until a real adapter implements same-volume staging, identity and reparse-point checks as appropriate, and atomic publication using the chosen overwrite policy. Hard links, ancestor links and path races need dedicated real-adapter tests.
- Temporary files are created exclusively in an application-created random temporary subdirectory. Dispose streams before cleaning the workspace.
- The process runner requires an explicitly configured executable path, a working directory, structured argument vectors and a finite timeout. It drains both streams with bounded capture and attempts tree termination on cancellation or timeout. It is an infrastructure primitive awaiting a real adapter.
- Runtime limits in M0 cover queue capacity, worker concurrency, log retention, argument limits, process deadline and captured output. Hard CPU, memory and child-process limits require OS-specific isolation. A child that detaches from a parent can outlive best-effort tree termination.
- Closing the desktop window drains cancellation before disposing the host. Job cancellation is cooperative.
