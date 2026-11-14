namespace UniversalConverter.Domain;

public enum OutputCollisionPolicy { Rename, Skip }

public static class OutputNaming
{
    public static string CreateFileName(string sourceName, FileFormat target, int suffix = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(suffix);
        var stem = Path.GetFileNameWithoutExtension(sourceName);
        return suffix == 0
            ? $"{stem}.{target.Extension}"
            : FormattableString.Invariant($"{stem} ({suffix}).{target.Extension}");
    }
}
