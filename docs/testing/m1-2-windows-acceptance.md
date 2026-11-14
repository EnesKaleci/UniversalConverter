# M1.2 Windows user acceptance

Use the Windows x64 Single-file **Unsigned Development/Test Build** for the exact CI commit under review. Check the inner ZIP against the accompanying SHA-256 manifest, extract it into a fresh folder and record the Windows version, commit, ZIP hash and result for every row. Keep Windows protections as configured. If Windows blocks the package, record the message and matching Code Integrity events, then mark the dependent checks SKIP. This development package has no signed Smart App Control acceptance.

Prepare a folder with several PNG, JPG and WebP files, one text file and a separate writable output folder. Include `İstanbul Fotoğraf.PNG`, `Iİıi.PNG` and `Çalışma öğeleri\ışık.webp`.

| # | Check | Expected behavior | Result |
| --- | --- | --- | --- |
| 1 | Launch from the extracted ZIP | The main window opens and identifies simulation mode. | PASS / FAIL / SKIP |
| 2 | Visit all six screens | Dashboard, Convert, Queue, History, Settings and About open. Empty screens explain what belongs there. | PASS / FAIL / SKIP |
| 3 | Select several files at once | All supported files appear with name, format, size and full path. Count and total size match the selection. | PASS / FAIL / SKIP |
| 4 | Drop files from Explorer | Supported files enter the same list. The window remains responsive. | PASS / FAIL / SKIP |
| 5 | Add a duplicate | The existing row stays once and a localized duplicate notice appears. | PASS / FAIL / SKIP |
| 6 | Drop a text file | The unsupported file stays out of the list and the app displays the reason. | PASS / FAIL / SKIP |
| 7 | Remove a single file | Select a row and press Delete. Only that row disappears. The source remains on disk. | PASS / FAIL / SKIP |
| 8 | Remove several files | Use Ctrl or Shift to select rows and choose Remove selected. Other rows and all sources remain. | PASS / FAIL / SKIP |
| 9 | Clear the list | Count and total reset to zero. Focus returns to the file picker. Sources remain. | PASS / FAIL / SKIP |
| 10 | Change target format | The preview updates for PNG, JPG and WebP. A same-format pair is flagged and excluded from the queue. | PASS / FAIL / SKIP |
| 11 | Select the output folder | The chosen folder appears in the UI. Returning to source folders works. A missing or unwritable folder reports a readable error. | PASS / FAIL / SKIP |
| 12 | Inspect output names | PNG to JPG previews `İstanbul Fotoğraf.jpg` and keeps the original Unicode stem. The source and target folder receive no output. | PASS / FAIL / SKIP |
| 13 | Test name conflicts | Seed `İstanbul Fotoğraf.jpg` in the target folder. Rename previews `İstanbul Fotoğraf (1).jpg`. Skip excludes the item. The existing file stays intact. | PASS / FAIL / SKIP |
| 14 | Queue several simulations | At most two jobs run at once. Per-job and overall progress update and terminal states arrive in History. No output is written. | PASS / FAIL / SKIP |
| 15 | Cancel a waiting job | Queue multiple supported files and immediately cancel a waiting row. History shows it as cancelled. | PASS / FAIL / SKIP |
| 16 | Cancel a running job | Cancel while a job is running. It stops and appears as cancelled in History. | PASS / FAIL / SKIP |
| 17 | Switch Turkish and English | Titles, new controls, existing notices and previews change live. | PASS / FAIL / SKIP |
| 18 | Use Unicode paths | Names with `i/İ/ı/I` and Turkish characters retain their spelling in list, preview and History. | PASS / FAIL / SKIP |
| 19 | Reopen with preferences | The output folder, language and saved theme survive closing and reopening. | PASS / FAIL / SKIP |
| 20 | Close during a batch | The process exits cleanly with source files intact and no simulated outputs. | PASS / FAIL / SKIP |
| 21 | Resize and navigate with the keyboard | Tab and arrow navigation, multiselect, Delete, long-name tooltips, scrolling and light/dark contrast remain usable. | PASS / FAIL / SKIP |
| 22 | Run `--diagnostics` | The diagnostics report SQLite, Skia and HarfBuzz success and produce a startup log. | PASS / FAIL / SKIP |

Output names are proposals. The app rechecks file access and collisions before queueing a simulation. The eventual conversion engine will need to reserve its output atomically at write time. Overall progress covers the jobs retained in the current queue and history, with terminal jobs counted as settled. Current Windows startup logs are under `%LOCALAPPDATA%\UniversalConverter\logs`.
