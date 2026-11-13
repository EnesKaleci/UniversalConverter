namespace UniversalConverter.Infrastructure;

public enum DesktopPlatform { Windows, MacOS, Linux, Unknown }

public static class PlatformDetection
{
    public static DesktopPlatform Current =>
        OperatingSystem.IsWindows() ? DesktopPlatform.Windows :
        OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS :
        OperatingSystem.IsLinux() ? DesktopPlatform.Linux : DesktopPlatform.Unknown;

}

public sealed class TemporaryWorkspace : IDisposable
{
    public string DirectoryPath { get; }

    public TemporaryWorkspace() =>
        DirectoryPath = Directory.CreateTempSubdirectory("UniversalConverter-").FullName;

    public FileStream CreateFile()
    {
        var path = Path.Combine(DirectoryPath, Path.GetRandomFileName());
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, recursive: true);
    }
}
