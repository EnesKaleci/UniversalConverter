#if WINDOWS_RELEASE_PROFILE
using System.Runtime.InteropServices;
#endif
using Microsoft.Data.Sqlite;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;
using System.Globalization;

namespace UniversalConverter.Infrastructure;

public sealed class SettingsDatabase : ISettingsStore, IOutputPreferences, IConversionHistory
{
    private readonly string _directory;
    private readonly string _connectionString;

#if WINDOWS_RELEASE_PROFILE
    static SettingsDatabase()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The release profile requires Windows System SQLite.");

        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_winsqlite3).Assembly,
            static (name, assembly, _) => name == "winsqlite3"
                ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"),
                    assembly, DllImportSearchPath.System32)
                : IntPtr.Zero);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    }
#endif

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

    public Task<string?> LoadOutputDirectoryAsync(CancellationToken cancellationToken = default) =>
        ReadAsync("outputDirectory", cancellationToken);

    public Task SaveOutputDirectoryAsync(string? path, CancellationToken cancellationToken = default) =>
        WriteAsync("outputDirectory", path ?? string.Empty, cancellationToken);

    public Task StoreAsync(ConversionHistoryEntry entry) => RunAsync(connection =>
    {
        EnsureHistory(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO image_history(id, source, target, output, status, error, finished, duration)
            VALUES($id, $source, $target, $output, $status, $error, $finished, $duration)
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString());
        command.Parameters.AddWithValue("$source", entry.SourcePath);
        command.Parameters.AddWithValue("$target", entry.Target);
        command.Parameters.AddWithValue("$output", (object?)entry.OutputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)entry.Status);
        command.Parameters.AddWithValue("$error", (object?)entry.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$finished", entry.FinishedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$duration", entry.Duration.Ticks);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM image_history WHERE rowid IN (SELECT rowid FROM image_history ORDER BY finished DESC, rowid DESC LIMIT -1 OFFSET 256)";
        return command.ExecuteNonQuery();
    }, default);

    public Task<IReadOnlyList<ConversionHistoryEntry>> LoadAsync() => RunAsync<IReadOnlyList<ConversionHistoryEntry>>(connection =>
    {
        EnsureHistory(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source, target, output, status, error, finished, duration FROM image_history ORDER BY finished DESC, rowid DESC LIMIT 256";
        using var reader = command.ExecuteReader();
        var entries = new List<ConversionHistoryEntry>();
        while (reader.Read())
            entries.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), (ConversionStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.ParseExact(reader.GetString(6), "O", CultureInfo.InvariantCulture), TimeSpan.FromTicks(reader.GetInt64(7))));
        return entries;
    }, default);

    private static void EnsureHistory(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS image_history (
                id TEXT PRIMARY KEY NOT NULL, source TEXT NOT NULL, target TEXT NOT NULL, output TEXT,
                status INTEGER NOT NULL, error TEXT, finished TEXT NOT NULL, duration INTEGER NOT NULL
            )
            """;
        command.ExecuteNonQuery();
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
