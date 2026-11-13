# Third-party licenses

Application source is GPL-3.0-or-later. The repository includes the unmodified GNU GPL v3 license text.

| Direct dependency family | Declared license | Upstream |
| --- | --- | --- |
| Avalonia, Desktop and Fluent Theme | MIT | [Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| CommunityToolkit.Mvvm | MIT | [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet) |
| Microsoft.Extensions.Hosting and DependencyInjection | MIT | [.NET Runtime](https://github.com/dotnet/runtime) |
| Microsoft.Data.Sqlite | MIT | [EF Core](https://github.com/dotnet/efcore) |
| SQLite native engine | Public domain | [SQLite copyright](https://sqlite.org/copyright.html) |
| Serilog hosting integration and file sink | Apache-2.0 | [Hosting](https://github.com/serilog/serilog-extensions-hosting), [file sink](https://github.com/serilog/serilog-sinks-file) |
| Microsoft.NET.Test.Sdk | MIT | [VSTest](https://github.com/microsoft/vstest) |
| xUnit and Visual Studio runner | Apache-2.0 | [xUnit](https://github.com/xunit/xunit), [runner](https://github.com/xunit/visualstudio.xunit) |

Exact direct versions are in Directory.Packages.props. Self-contained publish also ships .NET runtime components and native UI and SQLite dependencies. A release must inventory the resolved transitive packages and runtime/native files, retain their full license and notice texts, and record provenance and any corresponding-source obligations. This direct-dependency table is an initial inventory. M0 CI artifacts are for development acceptance pending the complete release inventory.
