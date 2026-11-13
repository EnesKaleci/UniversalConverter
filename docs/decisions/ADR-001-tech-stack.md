# ADR 001 · Technology stack

Accepted for M0. Use C# and .NET 10 LTS, Avalonia 11.x, CommunityToolkit.Mvvm, Generic Host and built-in dependency injection, Microsoft.Data.Sqlite, Serilog, and xUnit. Keep direct versions in Central Package Management. Publish an untrimmed self-contained Windows x64 directory to preserve native and managed dependencies.

Avalonia keeps desktop UI portable. SQLite keeps preferences local. A separate deployment decision will define supported Windows 10 editions and validation for macOS and Linux.
