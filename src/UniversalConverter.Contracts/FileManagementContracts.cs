namespace UniversalConverter.Contracts;

public interface IFileCatalog
{
    long ReadInputSize(string path);
    void ValidateDestination(string path);
    bool EntryExists(string path);
}

public interface IOutputPreferences
{
    Task<string?> LoadOutputDirectoryAsync(CancellationToken cancellationToken = default);
    Task SaveOutputDirectoryAsync(string? path, CancellationToken cancellationToken = default);
}
