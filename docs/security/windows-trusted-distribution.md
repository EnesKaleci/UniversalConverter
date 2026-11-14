# Windows trusted distribution

## Findings

| Build and evidence | Finding |
| --- | --- |
| M0 multi-file, real Windows 11 | .NET Runtime 1026 identified a FileLoadException with HRESULT 0x800711C7 while loading UniversalConverter.Infrastructure.dll in App.OnFrameworkInitializationCompleted. Code Integrity 3033 and enforced 3077 named policy {0283ac0f-fff1-49ae-ada1-8a933130cad6}. Microsoft lists this as Smart App Control's VerifiedAndReputableDesktop policy. The downloaded EXE was unsigned and had a Zone.Identifier stream. |
| M0 Single-file, real Windows user acceptance | The project owner reported 8/8 checks passed for that exact package. |
| M1 Single-file, commit 073b72373b00943df76170a427c9ae6450f7ab1c | The project owner reported a repeat block. The M1 event record, blocked path, required signing level and policy ID have yet to be collected. The old EXE and two native libraries were unsigned, so the first rejected file remains unknown. |

A 3077 event records an enforced policy block. A 3033 event can have other causes. Use the activity ID to associate 3089 signature details with its block event. Zone.Identifier records download provenance and may be used by reputation checks. File signatures, device policy and reputation require separate verification. [Microsoft inbox policy IDs](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/operations/inbox-appcontrol-policies) · [Code Integrity events](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/operations/event-id-explanations) · [Zone.Identifier](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/6e3f7352-d11c-4d76-8c39-2516a9df36e8)

| Windows protection | What it evaluates |
| --- | --- |
| Smart App Control | Windows 11 uses Microsoft's cloud predictions and trusted code signing to evaluate apps and code. The publisher and all separately loaded binaries matter. |
| App Control for Business (formerly WDAC) | An administrator can enforce organization-specific signer, file and hash rules. A Windows-trusted signature may still be rejected by a narrower allowlist or explicit deny rule. |
| Microsoft Defender SmartScreen | Separately checks downloaded app and publisher reputation. A new signed file may still show a reputation warning. Keep the verified publisher identity consistent across releases. |

[Smart App Control](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview) · [App Control for Business](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/appcontrol) · [SmartScreen reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)

## Package and native inventory

The M1 multi-file package shipped the EXE, managed assemblies, runtime configuration and native DLLs side by side. Its Infrastructure assembly was a separate unsigned PE file. The M1 Single-file bundle used .NET 10, win-x64, self-contained, without trimming, with PublishSingleFile and IncludeNativeLibrariesForSelfExtract enabled. Its manifest had 246 entries, including 240 managed assemblies and the tr-TR satellite, two JSON metadata files and four native DLLs. The managed Infrastructure assembly loads from the bundle. The native binaries are extracted to %TEMP%\.net\<app>\<bundle-id> by default on Windows and loaded as separate code. Authenticode on the EXE protects the bundle's bytes. Upstream embedded Authenticode on each extracted DLL covers that file as Windows loads it. The .NET native runtime is linked into the Windows Single-file host in the examined build. The M1 multi-file layout had separately Microsoft-signed .NET PE files such as hostfxr.dll, hostpolicy.dll, coreclr.dll, clrjit.dll, clrgc.dll, clrgcexp.dll, mscordaccore.dll and mscordbi.dll. [Microsoft .NET Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) · [.NET 10 Single-file host build](https://github.com/dotnet/runtime/blob/v10.0.15/src/native/corehost/apphost/static/CMakeLists.txt)

The following results come from the exact M1 Single-file bundle's EXE and native entries. ZIP SHA-256 is 5C750A9780C27B37DCFF4024F9300331C47B3A2844CD9040BA40027E9F9D2155.

| M1 file | Authenticode | SHA-256 (native entries) |
| --- | --- | --- |
| UniversalConverter.Desktop.exe | NotSigned | |
| av_libglesv2.dll (Avalonia ANGLE) | NotSigned | 9B203E40323B49DAD29546A52B8B67D200BBA8FF4CAB9709A79CEDE23BA847D4 |
| e_sqlite3.dll | NotSigned | B082E8A72AD4CE908234EAC970B09B7D328D16157CEC34A3EBCCFD9DFE2221C6 |
| libHarfBuzzSharp.dll | Valid, Microsoft Corporation | EB76238C9E8E41D44B5A5B18167C4C5B39CA5DB4277AF5DBE92D730F0FC14A7D |
| libSkiaSharp.dll | Valid, Microsoft Corporation | 9A0D95E8CAAA852C70D085AF6A40A744242172AD9EA3FD6BC7599875A8A1DBCD |

The Windows release profile pins Avalonia's newer upstream-signed ANGLE native package and uses Windows System32 winsqlite3.dll via Microsoft.Data.Sqlite.Core and the SQLitePCLRaw Windows provider. The application's SQLite schema and data directory are shared with the existing build. Windows Update services System SQLite. CI captures the exact file, version, SHA-256, signer, signature type and timestamp signer from a clean extraction and the operating system. A native DLL added in a future upgrade must be reviewed before updating the expected list. The existing layouts remain available as development CI builds. [Avalonia ANGLE signing change](https://github.com/AvaloniaUI/Avalonia/pull/21219) · [Published ANGLE package](https://www.nuget.org/packages/Avalonia.Angle.Windows.Natives/2.1.27548.20260419) · [Microsoft custom SQLite provider guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)

## Release design

The separate Windows trusted distribution workflow restores, builds and tests the release profile using a pinned SDK and pinned GitHub Actions. It extracts the real test ZIP and checks startup, SQLite, Skia, HarfBuzz, the GUI, graceful shutdown, Turkish and English localization and Unicode filenames. It requires valid embedded upstream signatures on the three extracted libraries, plus a valid Microsoft signature or catalog signature on System SQLite. The candidate ZIP is labeled Unsigned-Test. A non-tag run has read-only access and runs the negative signature check.

A protected vMAJOR.MINOR.PATCH tag pointing to a reviewed commit in main can enter the signing environment. SignPath receives an Actions artifact containing only the final built Single-file EXE. Its DLL contents keep their upstream signatures. The verifier requires a trusted Authenticode signature, the authorized signing certificate's SHA-256 fingerprint, the SHA-256 PE image and CMS digests, and a SHA-256 RFC 3161 token. The original PE bytes, including the bundle and embedded dependencies, must match the signer output apart from the PE checksum, certificate table metadata and standard alignment. Windows SDK verification uses signtool verify /pa /all /v and requires exit code 0. The flow then builds and verifies the final ZIP, records the corresponding source, native inventory, signer and timestamp, and emits a SHA-256 manifest. That exact ZIP enters Windows acceptance. Only the final job has GitHub release write permissions. [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool) · [Signing for Smart App Control](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control) · [App Control signer rule limits](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/select-types-of-rules-to-create)

Use a current public-trust code-signing certificate from a CA in the Microsoft Trusted Root Program and a provider-managed non-exportable key. This release verifier accepts RSA 2048–4096 for coverage of App Control publisher rules on managed PCs. Keep publisher identity consistent. Certificate renewal requires updating the approved fingerprint using the public certificate delivered by the CA and testing a candidate under the renewed certificate. Keep the token scoped to submitting requests for this project and policy. The provider and an authorized approver retain control of actual signing. [Microsoft Trusted Root participants](https://learn.microsoft.com/en-us/security/trusted-root/participants-list) · [SignPath GitHub origin verification](https://docs.signpath.io/trusted-build-systems/github)

## Enable the release workflow

1. Obtain provider approval. The workflow currently supports SignPath. Before activating the SignPath Foundation option, have it confirm that the .NET Single-file EXE containing upstream .NET/Avalonia code and the three vendor-signed native DLLs is eligible. Ask about the archived development ZIPs given the prior-release requirement. No application or purchase has been made.
2. Configure SignPath's GitHub.com trusted build origin for this repository, this workflow and the reviewed release tag policy. Permit only the project EXE. Enable SHA-256 Authenticode with SHA-256 RFC 3161 timestamps. Use a release-approver role, a submit-only integration token and MFA. In the provider artifact configuration use the schema below and enforce the product name Universal Converter and expected product version from the reviewed build. Publish the actual provider and maintainer roles in the project code signing policy when an agreement is in place. [SignPath Foundation terms](https://signpath.org/terms) · [SignPath artifact configuration](https://docs.signpath.io/artifact-configuration/)
3. Protect main with reviewed changes. Add an active GitHub ruleset for tags v* that restricts tag creation to release maintainers and blocks tag moves and deletion. Pre-create windows-signing and windows-release as GitHub deployment environments. Restrict both to protected release tags, require named environment reviewers, prevent self-review and disable bypass. Schedule signing approval while the job waits for the provider. Only the windows-signing environment receives the signing token. [GitHub rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets) · [GitHub deployment environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
4. Set the variables and secret below. Obtain the SHA-256 fingerprint directly from the provider-approved publisher certificate. Do not commit keys, PFX files, passwords or service credentials. Use the project account and provider interfaces.
5. Review the transitive license/notice inventory and source obligations before releasing. Create a protected version tag on reviewed main after signing authority is ready. Download the Windows-x64-Signed-Acceptance-Candidate artifact for the real Windows protocol below. Verify the hash of the inner ZIP. When acceptance is complete, an authorized windows-release reviewer approves the job and enters WINDOWS-SAC-PASS followed by a space and the exact uppercase ZIP SHA-256 in the approval comment. The workflow verifies the recorded approval and exact bytes before publishing. If staging the GitHub Release succeeds but publication fails, inspect the draft and verified asset hashes before resuming. [License inventory](../legal/third-party-licenses.md)

~~~xml
<zip-file>
  <pe-file path="UniversalConverter.Desktop.exe">
    <authenticode-sign hash-algorithm="sha256" />
  </pe-file>
</zip-file>
~~~

| GitHub windows-signing setting | Value |
| --- | --- |
| Variable WINDOWS_SIGNING_PROVIDER | signpath |
| Variables SIGNPATH_ORGANIZATION_ID, SIGNPATH_PROJECT_SLUG | Authorized SignPath organization and project |
| Variables SIGNPATH_POLICY_SLUG, SIGNPATH_ARTIFACT_SLUG | Release approval policy and EXE-only artifact configuration |
| Variable WINDOWS_SIGNING_CERT_SHA256 | 64 hexadecimal characters from the issued public signing certificate |
| Secret SIGNPATH_API_TOKEN | Provider-issued, project/policy-scoped submit-only token |

## Provider comparison

Checked 2026-11-14. USD amounts are indicative before local taxes or fees.

| Provider | Cost and eligibility from Türkiye | GitHub Actions and key handling | Native and residual constraints |
| --- | --- | --- | --- |
| SignPath Foundation | No fee for approved open-source projects. Requires OSI licensing, ongoing maintenance, an existing release in the distribution form requested, a documented download page, verifiable reputation and project approval. No country exclusion for Türkiye is stated in the published terms. Approval for this project and its release format is pending. | GitHub-hosted source build, origin verification, a submit-only token, manually approved signatures, provider HSM and MFA. Certificate publisher is SignPath Foundation. The release workflow includes this integration. | Only project-owned output may be submitted for signing. Third-party DLLs keep their vendor signatures. Obtain written confirmation for the exact Single-file bundle. Smart App Control and SmartScreen still need the actual package tested. [Terms](https://signpath.org/terms) · [GitHub integration](https://docs.signpath.io/trusted-build-systems/github) |
| SSL.com IV/OV with eSigner | IV supports individual identity validation and OV requires organization validation. The Turkish IV product page exists and the CA's published country code list includes TR. Confirm Turkish ID/entity acceptance for this specific product and approval of the intended publisher name before payment. IV/OV advertises $129/year. Tier 1 eSigner lists $15/month on the annual option, 240 signatures and one credential. Confirm billing and local charges. | Cloud HSM and eSigner CKA/SignTool or the provider's CI integration. A separately reviewed provider step is required. Avoid passing passwords or second-factor seeds in logged command lines. | Sign the application-owned EXE only. Verify chain, RSA, timestamp and every loaded native dependency. [IV product](https://www.ssl.com/tr/products/software-integrity/code-signing/iv/) · [Certificate country list](https://reminder.ssl.com/csrs/country_codes) · [eSigner pricing and integration](https://www.ssl.com/products/software-integrity/signing-service/) |
| Sectigo OV/EV | Public CA exclusion list omits Türkiye and a Turkish reseller offers Sectigo code signing. Confirm acceptable Turkish legal status, evidence and delivery with the CA/reseller. Sectigo lists from $536.25/year for the five-year option, with higher prices for shorter terms or EV. | A non-exportable token or a supported, attested HSM. CI integration depends on the selected provider, HSM and driver. Confirm unattended operation in a protected signing job. | Sign the application-owned EXE and keep upstream DLL signatures intact. EV does not guarantee SmartScreen reputation. [CA country exclusions](https://www.sectigo.com/knowledge-base/detail/Banned-Country-List-1527076085907) · [CA code signing](https://www.sectigo.com/ssl-certificates-tls/code-signing) · [Turkish availability](https://kodimza.com/kod-imzalama-sertifikasi) |
| DigiCert Code Signing + KeyLocker | Turkish account/identity eligibility and a firm local quote have yet to be confirmed for this product. The public store lists a $996 auto-renewing subscription with 1,000 signings/year and also shows a separate monthly figure. Confirm the actual price with DigiCert. | Official GitHub Actions integration and FIPS 140-2 cloud key storage. Limit service-user access to the exact key and signing operation. | Sign only the project EXE. Keep the common verification, deployment and native signature checks. [DigiCert pricing](https://www.digicert.com/buy) · [KeyLocker GitHub Actions](https://docs.digicert.com/en/digicert-keylocker/ci-cd-integrations-and-deployment-pipelines/plugins/github/binary-signing-using-github-actions.html) |
| Microsoft Azure Artifact Signing | Ineligible for a Türkiye-based individual or a Türkiye-established organization under the current Public Trust geography. Individuals must be in the US or Canada. Organizations are accepted in the US, Canada, EU, UK, Australia, New Zealand, Japan, South Korea, Singapore, Switzerland, Norway and Israel. Basic lists 5,000 and Premium 100,000 signatures/month. Microsoft's developer overview indicates about $9.99/month for Basic. | Microsoft manages certificate and key rotation. Official GitHub Action supports Microsoft Entra federation and a scoped Certificate Profile Signer role. | Private Trust has a different trust boundary and cannot establish public Windows trust for the portable package. A future public-trust adapter would need to account for certificate rotation. [Current identity eligibility](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) · [Azure pricing](https://azure.microsoft.com/en-us/pricing/details/artifact-signing/) · [Cost overview](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) · [GitHub OIDC](https://github.com/Azure/artifact-signing-action/blob/main/docs/OIDC.md) |

If the project receives an eligible SignPath approval, use the configured integration. If Foundation eligibility or bundle approval is unavailable, request a written SSL.com IV quote and confirmation for a Turkish individual before paying. A commercial provider adapter may use its supported HSM-backed SignTool integration with /fd SHA256 /tr and /td SHA256, then return the signed EXE to the same verifier. Issuance and publisher continuity matter more than choosing EV for SmartScreen. [Microsoft signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)

## Real Windows acceptance

Record the ZIP SHA-256, source commit, Windows 11 edition and build, Smart App Control status On, and the exact downloaded ZIP. Use a normal browser download and Windows Explorer extraction to a clean folder. Preserve any attached Zone.Identifier and leave Windows protections and policies as configured. Check the EXE publisher and timestamp and run the checks below as a standard user.

1. Start from the extracted folder and confirm that the main window opens normally with no security prompt or Code Integrity enforcement block for the app or extracted files.
2. Run --diagnostics. Check the SQLite, Skia and HarfBuzz PASS markers in the startup log.
3. Repeat the eight [M0 checks](../testing/test-strategy.md) on the signed candidate. Switch Turkish and English and review all six screens, then close and reopen to verify SQLite preference and theme persistence. Pick and queue a Unicode path such as Şehir\İzmir_ışık_Çığ.png.
4. Install the next signed candidate from a fresh download and folder, with the same publisher and existing app data. Check that preferences survive and the new hash opens as well. On machines with organization policies, record those policies separately.
5. If Windows blocks the launch, record the exact file and the corresponding 3077/3033/3089 XML (correlate by ActivityID), plus .NET Runtime 1026 and startup logs if they exist. Check event scope since the recorded launch time, and retain the artifact SHA-256. A pre-start EXE block can leave no application log.

~~~powershell
Get-FileHash .\UniversalConverter-0.1.0-Windows-x64-SingleFile.zip -Algorithm SHA256
Get-AuthenticodeSignature -LiteralPath .\UniversalConverter.Desktop.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
Get-Item -LiteralPath .\UniversalConverter.Desktop.exe -Stream Zone.Identifier -ErrorAction SilentlyContinue
CiTool.exe -lp
Get-WinEvent -FilterHashtable @{
    LogName = 'Microsoft-Windows-CodeIntegrity/Operational'
    Id = 3033,3077,3089
    StartTime = (Get-Date).AddMinutes(-15)
} | Select-Object -First 30 | ForEach-Object { $_.ToXml() }
~~~

Startup logs are under %LOCALAPPDATA%\UniversalConverter\logs with a fallback to %TEMP%\UniversalConverter\logs. The hosted Windows Server runner verifies the software and signatures under its own policy. Smart App Control acceptance requires the signed candidate on a real Windows 11 client with Smart App Control On. Until that evidence exists, public distribution is NO-GO.
