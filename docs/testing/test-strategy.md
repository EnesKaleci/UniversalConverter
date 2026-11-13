# Test strategy

Core xUnit tests cover format and job invariants, architecture references, planning, bounded queue behavior, progress, failures, cancellation and shutdown. Infrastructure tests cover SQLite settings, temporary files, process allowlisting and simulation behavior. Synchronization gates drive core queue tests, with deadlines for deadlock detection.

Windows CI builds, tests and publishes a self-contained x64 directory. It checks the published executable opens a Windows main window with the expected title and closes successfully. TRX files report the actual xUnit results. A Windows CI window check provides startup evidence on the runner.

## Windows user acceptance

1. Extract the portable ZIP. Launch the EXE and confirm Dashboard opens.
2. Visit all six screens. Check resizing and scrolling in light and dark.
3. Add PNG, JPG and WebP paths using the picker and Explorer drag-and-drop. Confirm unsupported files and duplicate paths are skipped.
4. Queue multiple valid pairs. Observe progress and no more than two running jobs. Cancel a running job and a waiting job.
5. Check History for completed simulations and cancellations. Confirm no output files were created.
6. Save a theme and relaunch. Confirm the choice persists.
7. Close the app during active work. Confirm the process exits and relaunch works.
8. Record Windows edition, build and test outcomes, including any Windows security prompt.

User acceptance is required for final M0 GO.
