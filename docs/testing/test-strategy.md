# Test strategy

Core xUnit tests cover format and job invariants, architecture references, planning, bounded queue behavior, progress, failures, cancellation and shutdown. Infrastructure tests cover SQLite settings, temporary files, process allowlisting and simulation behavior. Synchronization gates drive core queue tests, with deadlines for deadlock detection.

Windows CI builds and runs xUnit, then publishes both self-contained Windows x64 layouts. It verifies extracted ZIP files against the publish output, records signatures and ZIP SHA-256, and runs diagnostics and GUI smoke tests from the extracted packages with separate clean data directories. It verifies SQLite initialization, startup logs and graceful shutdown. The single-file check inspects native extraction. The multi-file check also tests diagnostics with a missing Infrastructure DLL. TRX files report actual xUnit results.

The hosted runner establishes packaging and startup behavior under its own configuration. It does not establish behavior for internet-downloaded artifacts under an end user's App Control policy. Follow the [Windows App Control retest procedure](../security/windows-app-control.md) when reproducing a policy block.

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

## M1 localization acceptance on Windows Single-file

1. Extract the Single-file ZIP and launch the executable. For a clean-data test, set `CONVERTER_DATA_DIRECTORY` in the launching PowerShell session to a new absolute folder. Verify **Genel Bakış** on a Turkish display-language account and **Dashboard** on a non-Turkish account.
2. Open **Settings / Ayarlar** and switch between **English** and **Türkçe** without closing the window. Visit all six screens in each language. Check the page title, sidebar, theme options, file-picker title, simulation banner and status messages.
3. Under a folder named `Çalışma öğeleri`, select or drop a valid image named `İşık ışıĞı Şüphe.PNG`. Queue a JPG simulation and check the filename in the queue and history. Switch languages with a job or status message present. Check that the text changes and the filename retains its exact spelling.
4. Choose Türkçe, close and reopen. Confirm the choice persists and repeat in English. Run the M0 Windows user acceptance checks for the same artifact.

Windows CI checks the actual extracted executable using Windows UI Automation. It selects both languages in the same window, checks all six page titles and labels, drives an example Turkish path through the picker and queue, and verifies that Turkish is restored after restart. Unit tests cover initial culture selection, resource fallback, Turkish casing, UTF-8, settings persistence and in-flight queue state. The hosted runner provides automated results. Record user acceptance separately on the target Windows machine.
