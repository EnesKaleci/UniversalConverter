using Microsoft.Data.Sqlite;
using UniversalConverter.Contracts;

namespace UniversalConverter.Infrastructure;

public sealed class SettingsDatabase : ISettingsStore
{
    private readonly string _directory;
    private readonly string _connectionString;

    public SettingsDatabase(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        _directory = Path.GetDirectoryName(path)!;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 3
        }.ToString();
    }

    public async Task<ThemePreference> LoadThemeAsync(CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync("theme", cancellationToken).ConfigureAwait(false);
        return Enum.TryParse<ThemePreference>(value, out var theme) && Enum.IsDefined(theme)
            ? theme : ThemePreference.System;
    }

    public Task SaveThemeAsync(ThemePreference theme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme));
        return WriteAsync("theme", theme.ToString(), cancellationToken);
    }

    public Task<string?> LoadLanguageAsync(CancellationToken cancellationToken = default) =>
        ReadAsync("language", cancellationToken);

    public Task SaveLanguageAsync(string languageTag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageTag);
        return WriteAsync("language", languageTag, cancellationToken);
    }

    private Task<string?> ReadAsync(string key, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }, cancellationToken);

    private Task WriteAsync(string key, string value, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings(key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            return command.ExecuteNonQuery();
        }, cancellationToken);

    private Task<T> RunAsync<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var initialize = connection.CreateCommand();
            initialize.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)
                """;
            initialize.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            return operation(connection);
        }, cancellationToken);
}
