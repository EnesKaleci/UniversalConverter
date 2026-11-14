# UniversalConverter

Free, open-source, cross-platform universal file converter.

Universal Converter is an offline-first desktop application targeting Windows first. macOS and Linux validation, native installers and real conversion engines are planned.

## M0 foundation

M0 simulates conversions between PNG, JPG and WebP. It reads the selected path and extension to prepare a job. It creates no converted files. Use **Convert** to choose or drop files, select a target and queue simulations. **Queue** shows progress and offers cancellation. **History** lists recent results from the current session. **Settings** saves the system, light or dark theme to the local SQLite database.

## Interface language

Open **Settings → Language** (**Ayarlar → Dil**) and select **English** or **Türkçe**. The six screens update immediately and the preference is saved to the local SQLite database. On first launch, the app uses Turkish for a Turkish Windows display language and English for other languages.

İlk açılışta uygulama Windows görüntüleme dilini kullanır. Türkçe dışındaki sistem dillerinde İngilizce açılır. **Ayarlar → Dil** bölümünden seçtiğin dil anında uygulanır ve sonraki açılışta korunur.

## Windows trusted distribution

The Windows release profile uses a self-contained Single-file build with upstream-signed graphics libraries and Windows System SQLite. Actions artifacts marked **Unsigned-Test** are for CI and development. Read the [code signing policy, provider comparison and Windows acceptance checklist](docs/security/windows-trusted-distribution.md) before distributing a build.

## Build and run

Install the .NET 10 SDK. From the repository root:

```powershell
dotnet restore UniversalConverter.slnx
dotnet build UniversalConverter.slnx
dotnet test UniversalConverter.slnx --no-build
dotnet run --project src/UniversalConverter.Desktop
```

For a self-contained Windows x64 build:

```powershell
dotnet publish src/UniversalConverter.Desktop -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o artifacts/publish
```

Extract the CI portable ZIP and run `UniversalConverter.Desktop.exe`. Keep the multi-file package's contents together. Both Windows x64 layouts are self-contained. The single-file build extracts native libraries to a per-user cache at startup. CI builds are unsigned test artifacts.

Run `Start-Process -FilePath .\UniversalConverter.Desktop.exe -ArgumentList '--diagnostics' -NoNewWindow -Wait` in PowerShell for a pre-GUI check. Startup logs are under `%LOCALAPPDATA%\UniversalConverter\logs`, with a fallback under `%TEMP%\UniversalConverter\logs`. See [Windows startup, App Control and signing](docs/security/windows-app-control.md).

- [Architecture](docs/architecture/overview.md)
- [Conversion pipeline](docs/architecture/conversion-pipeline.md)
- [Test strategy and Windows acceptance checklist](docs/testing/test-strategy.md)
- [License notices](docs/legal/third-party-licenses.md)
- [Roadmap](docs/roadmap.md)

Universal Converter is available under GPL-3.0-or-later. See [LICENSE](LICENSE).
