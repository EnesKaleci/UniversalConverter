# M2 Windows user acceptance

Use the Windows x64 Single-file **Unsigned Development/Test Build**. Record the tested commit, inner ZIP SHA-256, Windows version and the result of every check as PASS, FAIL or SKIP. Verify the inner ZIP manifest and extract into a clean folder. Leave Windows security settings as configured. A Windows security block should be recorded separately with its error and Code Integrity event. M7 covers signed distribution and Smart App Control acceptance.

| # | Check | Expected result |
| --- | --- | --- |
| 1 | Start the extracted EXE | The app opens and the six screens are available. |
| 2 | Run --diagnostics | SQLite, Skia and HarfBuzz checks succeed. |
| 3 | PNG → JPEG | The JPEG opens and has the expected resolution. |
| 4 | PNG → WebP | The WebP opens and has the expected resolution. |
| 5 | JPEG → PNG | The PNG opens and has the expected resolution. |
| 6 | JPEG → WebP | The WebP opens and has the expected resolution. |
| 7 | WebP → PNG | The PNG opens and has the expected resolution. |
| 8 | WebP → JPEG | The JPEG opens and has the expected resolution. |
| 9 | Choose a .jpeg source and the .jpeg output option | The app recognizes JPEG inputs and creates a .jpeg file for the explicit target. |
| 10 | Choose the source format as target | The pair is rejected. |
| 11 | Compare JPEG quality 5 and 95 | Both files open and their encoded content differs. |
| 12 | Compare WebP quality 5 and 95 | Both files open and their encoded content differs. |
| 13 | Choose PNG as target | The quality slider is hidden and output uses lossless PNG encoding. |
| 14 | Convert a transparent PNG or WebP to JPEG | The warning appears and the transparent background becomes white. |
| 15 | Convert PNG → WebP → PNG with transparent and partially transparent pixels | Alpha survives both conversions. |
| 16 | Convert a JPEG with EXIF orientation 6 | The result is visually upright and width and height swap. |
| 17 | Use İstanbul Fotoğraf.PNG, Iİıi.PNG and Şüphe ve Çığ.jpeg | Unicode filenames and original stems are preserved. |
| 18 | Change the target folder | Outputs appear in the selected writable directory and the preference survives restart. |
| 19 | Select a missing or read-only folder | An understandable error appears and no output is written. |
| 20 | Run Rename with an occupied destination name | An available numbered name is used and the existing file hash is unchanged. |
| 21 | Run Skip with an occupied destination name | History shows Skipped without a generated output path. |
| 22 | Batch two sources with the same stem from different directories | Both outputs use distinct names and both are readable. |
| 23 | Queue at least three images | No more than two jobs run at once and the queue displays live state, cancellation and up to 20 recent outcomes. |
| 24 | Cancel a waiting job | It becomes Cancelled and no output exists. |
| 25 | Cancel a running job before final placement | It becomes Cancelled and no final or application-owned temporary file remains after the codec returns. |
| 26 | Queue broken, empty and mismatched image files | Failed jobs show a localized summary without publishing a file. |
| 27 | Queue an animated WebP or APNG | It is rejected as animated. |
| 28 | Queue an image exceeding the documented limit | It fails gracefully with a limit message. |
| 29 | Compare source hashes before and after all conversions | Every source hash matches, including with the source folder used as the target. |
| 30 | Open a successful result's location | The output folder opens. A removed result yields a readable error. |
| 31 | Restart the app | Real history, outcome, output path, completion time and duration remain. Simulations are scoped to the previous session. |
| 32 | Run a simulation | The simulation appears in its own history section and creates no file. |
| 33 | Switch Turkish and English and reload preferences | New controls, warnings and history localize live. The language and theme survive restart. |
| 34 | Repeat M1.2 file management and close during a batch | Multiple selection, Explorer drop, removal, duplicates, preview and folder preferences still work. The process exits after conversion cleanup. |

## Codec and filesystem behavior

The app uses the SkiaSharp version already used by Avalonia. Real conversion allows a source file up to 64 MiB, up to 20 million decoded pixels and up to 16,384 pixels on each axis. JPEG and lossy WebP default to 85 on a 1–100 scale. PNG has no quality control. Output is normalized to sRGB and baked to its visible orientation. Source metadata is omitted from the output. Encoders, decoders and the final filesystem move can take time. Cancellation is checked before and after native image work, between write chunks and before committing. Once placement begins, it is allowed to finish.

The app flushes and verifies the temporary image in the destination directory before attempting a no-overwrite move. Local Windows volumes support safe single-name publication through a same-directory rename. Filesystems and remote shares have their own rename and durability semantics. Existing destination links are treated as conflicts, and the app rejects linked destination directories. Keep the destination under your control while jobs run. Power loss or process termination can leave a temporary file. If permissions change during cleanup, the app reports a destination failure.

The app keeps the latest 256 real conversions in local SQLite, including failed, cancelled and skipped jobs. Simulation history lasts for the current session. See the [M1.2 checklist](m1-2-windows-acceptance.md) and the [trusted distribution plan](../security/windows-trusted-distribution.md).
