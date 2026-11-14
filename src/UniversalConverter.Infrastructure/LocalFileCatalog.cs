using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UniversalConverter.Contracts;

namespace UniversalConverter.Infrastructure;

public sealed class LocalFileCatalog : IFileCatalog
{
    public long ReadInputSize(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            throw new IOException("The selected entry is a directory.");

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return file.Length;
    }

    public void ValidateDestination(string path)
    {
        if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            throw new IOException("The destination is not a directory.");

        if (OperatingSystem.IsWindows())
        {
            // FILE_ADD_FILE verifies directory access without creating a probe file.
            using var handle = CreateFile(path, 2, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new UnauthorizedAccessException("The directory could not be opened for file creation.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else if (Access(path, 3) != 0) // W_OK | X_OK
        {
            throw new UnauthorizedAccessException("The directory is not writable and searchable.");
        }
    }

    public bool EntryExists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return new FileInfo(path).LinkTarget is not null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("libc", EntryPoint = "access", ExactSpelling = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
