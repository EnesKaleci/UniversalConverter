# Windows startup, App Control and signing

## Confirmed M0 failure

The Windows 11 Application log recorded .NET Runtime event 1026 with a `FileLoadException`, HRESULT `0x800711C7` and the path to `UniversalConverter.Infrastructure.dll`. CoreCLR reached Avalonia framework initialization. The multi-file apphost started, loaded the desktop application and entered Avalonia. The desktop composition method directly references the Infrastructure assembly for SQLite settings and the simulation engine. CLR resolution of that dependency was blocked during framework initialization. The source line identifies the call stack location, and dependency resolution may occur while the method is being JIT-compiled.

Code Integrity 3033 reported a signing-level mismatch for that file. Matching 3077 records identify an enforced policy with ID `{0283AC0F-FFF1-49AE-ADA1-8A933130CAD6}`. This GUID is associated with the Windows inbox `VerifiedAndReputableDesktop` policy. Confirm the friendly name and enforced state on the affected device with the read-only `CiTool.exe -lp` command and the matching 3077 or policy-load 3099 XML. Correlate 3089 with the 3077 Activity ID to inspect the rejected file's signatures. The reported EXE was `NotSigned`. Its signature status alone cannot establish the DLL's signature chain or the exact rule or reputation result that rejected it.

Smart App Control is the Windows 11 consumer protection built on App Control. App Control for Business can apply administrator-managed policies with different publisher and trust rules. An enforced 3077 confirms the file-level policy decision. SmartScreen separately evaluates reputation for downloaded files. The observed `Zone.Identifier` stream is origin metadata (Mark of the Web). Its presence does not establish the condition that caused this Code Integrity block.

Sources: [App Control and Smart App Control](https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/appcontrol), [Code Integrity event IDs](https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/operations/event-id-explanations), [policy verification](https://learn.microsoft.com/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control), [CiTool](https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/operations/citool-commands), [SmartScreen reputation](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation), [Zone.Identifier](https://learn.microsoft.com/openspecs/windows_protocols/ms-fscc/6e3f7352-d11c-4d76-8c39-2516a9df36e8).

## Windows x64 deployment layouts

| Property | Multi-file | Single-file |
| --- | --- | --- |
| Runtime | .NET 10 self-contained, win-x64 | .NET 10 self-contained, win-x64 |
| Managed application DLLs | Side by side, including Infrastructure | Bundled in the EXE |
| Native dependencies | Side by side | Bundled with `IncludeNativeLibrariesForSelfExtract=true`, then extracted before managed entry |
| File placement | Keep all extracted files together | One executable plus ancillary documentation and metadata in the ZIP |
| Windows policy | Files loaded from disk remain subject to policy | The EXE and extracted native code remain subject to policy |

Single-file CI verifies that Skia, HarfBuzz and SQLite native DLLs extract to a fresh test cache and that Infrastructure is not extracted as a DLL. Windows defaults to a cache under `%TEMP%\.net`. The CI-only cache override makes the check repeatable. Native extraction and host startup can fail before the managed logger runs.

Sources: [.NET single-file deployment](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview), [.NET Windows support](https://learn.microsoft.com/dotnet/core/install/windows).

## Startup diagnostics

Run this in PowerShell from the fully extracted package:

```powershell
$p = Start-Process -FilePath .\UniversalConverter.Desktop.exe -ArgumentList '--diagnostics' -NoNewWindow -Wait -PassThru
$p.ExitCode
```

Diagnostics runs before Avalonia and checks the package layout, Skia and HarfBuzz native calls, data directory write access and the SQLite settings database. Exit codes are 0 for success, 1 for fatal startup or configuration failure, 2 for failed dependency or settings checks, and 3 for a caught Windows Application Control block. Exit codes apply when managed code runs. A corrupt settings database is logged and preserved. The GUI falls back to the system theme if loading preferences fails.

Find the newest `startup-*.log` under `%LOCALAPPDATA%\UniversalConverter\logs`, or under `%TEMP%\UniversalConverter\logs` if the primary log directory is inaccessible. The log records stages, OS and process architecture, runtime and application versions, exception types, HRESULTs and stack frames. It omits raw exception messages and source file paths. Normal Windows startup failures can display a native dialog after managed code starts. When the OS blocks the EXE, the runtime or bundle extraction before managed entry, use the OS event records and PowerShell launch error.

## Signed distribution roadmap

1. Choose a public-trust Authenticode code-signing provider that can verify the publisher and issue a certificate chaining to a Windows-trusted root. Check regional eligibility before choosing Azure Artifact Signing Public Trust. Its individual offering currently requires a US or Canadian location. A public CA that verifies the publisher in the applicable country is the alternative. For compatibility with App Control for Business signer rules, choose RSA up to 4096 bits and confirm the required trust chain with the target organization.
2. Store the signing key in a managed service or protected hardware. Run release signing from a protected pipeline with scoped credentials and publish only after verification. Preserve valid vendor signatures and document the provenance of every redistributed native runtime or third-party binary.
3. Multi-file: sign the apphost and the project's managed PE DLLs, including Infrastructure, and verify all shipped PE files. Review unsigned native dependencies and obtain or produce verifiably signed distributions where the target policy requires them. Single-file: prepare and verify bundled native signatures before bundling, using the SDK bundling extension point if needed. Authenticode-sign the final EXE after bundling. Signing the outer EXE does not sign native files extracted from it.
4. Use SHA-256 and RFC 3161 timestamping. Verify Authenticode signatures with `signtool verify /pa /all /v`, review the publisher and timestamp, package the verified bytes, and publish the ZIP SHA-256 and signing inventory. A valid signature establishes publisher identity and integrity under the verification policy. App Control can still apply stricter policy, and SmartScreen reputation can differ.
5. Retest a browser-downloaded, timestamped signed ZIP on Windows 11 with the affected policy enforced and on any supported Windows 10 LTSC or Enterprise target before claiming compatibility. Keep current unsigned CI builds labeled as test artifacts.

Sources: [Smart App Control code signing](https://learn.microsoft.com/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control), [Artifact Signing eligibility](https://learn.microsoft.com/azure/artifact-signing/quickstart), [App Control signer rules](https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/design/select-types-of-rules-to-create), [SignTool](https://learn.microsoft.com/windows/win32/seccrypto/signtool).

## Windows user retest

1. Record the Windows edition, build and architecture. In an elevated terminal if required, run `CiTool.exe -lp` and record the matching policy's GUID, friendly name and enforced state. Keep existing security settings and download metadata.
2. Download the test ZIP via the browser, compare the inner ZIP's SHA-256 and extract it to a new folder. Check `Get-AuthenticodeSignature` for the EXE and, in the multi-file layout, for `UniversalConverter.Infrastructure.dll`. Use the included `authenticode.csv` to compare other PE files.
3. Run each layout once normally and once with `--diagnostics`. Record the exit code, whether the window opens, the last startup stage and the newest log. On a block, capture the launch time and the relevant CodeIntegrity Operational 3033/3077/3089 XML and .NET Runtime 1026 entry if present. Share only relevant fields after removing personal paths. If there is no startup log, record that too.
4. For any layout that opens, complete the Windows acceptance checklist, including the six screens, drag-and-drop, queue, cancellation, history, theme persistence and shutdown.

GitHub-hosted Windows CI checks package integrity and runtime behavior on its runner. It does not reproduce the affected Windows 11 policy or download reputation. The startup check does not grant release acceptance.
