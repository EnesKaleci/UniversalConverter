using System.Diagnostics;

namespace UniversalConverter.Infrastructure;

public static class OutputLocation
{
    public static void Open(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("The output is no longer available.");

        var file = new FileInfo(path);
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.LinkTarget is not null)
            throw new IOException("The output is a link.");

        var folder = file.Directory ?? throw new IOException("The output folder is unavailable.");
        for (var parent = folder; parent is not null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint) || parent.LinkTarget is not null)
                throw new IOException("The output folder is a link.");

        using var operation = Process.Start(new ProcessStartInfo(folder.FullName) { UseShellExecute = true });
    }
}
