using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Application;

public sealed record InputFile(string Path, FileFormat Format, long Bytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public enum InputIssue { Duplicate, Unsupported, Missing, AccessDenied, Limit }
public enum OutputIssue { PairUnsupported, SourceUnavailable, InvalidFolder, UnwritableFolder, Conflict, Unavailable }

public sealed record InputBatch(IReadOnlyList<InputFile> Files, IReadOnlyDictionary<InputIssue, int> Issues);
public sealed record OutputProposal(InputFile Input, string? Path, OutputIssue? Issue = null);

public sealed class FilePreparation(IFileCatalog files, IFormatDetector detector, IConversionEngineRegistry registry)
{
    public const int InputLimit = 128;

    private static readonly StringComparer Paths =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Task<InputBatch> InspectAsync(IReadOnlyList<string> paths, IReadOnlyList<InputFile> existing,
        CancellationToken token = default) => Task.Run(() =>
    {
        var seen = new HashSet<string>(existing.Select(input => input.Path), Paths);
        var added = new List<InputFile>();
        var issues = new Dictionary<InputIssue, int>();

        void Reject(InputIssue issue) => issues[issue] = issues.GetValueOrDefault(issue) + 1;

        foreach (var supplied in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = PathSafety.FullPath(supplied);
                var format = detector.Detect(path);
                if (format is null)
                    Reject(InputIssue.Unsupported);
                else if (seen.Contains(path))
                    Reject(InputIssue.Duplicate);
                else if (existing.Count + added.Count >= InputLimit)
                    Reject(InputIssue.Limit);
                else
                {
                    added.Add(new(path, format, files.ReadInputSize(path)));
                    seen.Add(path);
                }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                Reject(InputIssue.Missing);
            }
            catch (Exception error) when (IsFileError(error))
            {
                Reject(InputIssue.AccessDenied);
            }
        }

        return new InputBatch(added, issues);
    }, token);

    public Task<string> ValidateDirectoryAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        return ValidateDirectory(path);
    }, token);

    public Task<IReadOnlyList<OutputProposal>> PlanAsync(IReadOnlyList<InputFile> inputs, FileFormat target,
        string? directory, OutputCollisionPolicy policy, CancellationToken token = default, bool recheckInputs = false) =>
        Task.Run<IReadOnlyList<OutputProposal>>(() =>
        {
            if (!Enum.IsDefined(policy))
                throw new ArgumentOutOfRangeException(nameof(policy));

            var reserved = new HashSet<string>(inputs.Select(file => file.Path), Paths);
            var directories = new Dictionary<string, OutputIssue?>(Paths);
            var proposals = new List<OutputProposal>();

            foreach (var input in inputs)
            {
                token.ThrowIfCancellationRequested();
                if (!registry.Engines.Any(engine => engine.Capabilities.Any(pair => pair.Matches(input.Format, target))))
                {
                    proposals.Add(new(input, null, OutputIssue.PairUnsupported));
                    continue;
                }

                if (recheckInputs)
                {
                    try { files.ReadInputSize(input.Path); }
                    catch (Exception error) when (IsFileError(error))
                    {
                        proposals.Add(new(input, null, OutputIssue.SourceUnavailable));
                        continue;
                    }
                }

                var folder = directory ?? Path.GetDirectoryName(input.Path)!;
                if (!directories.TryGetValue(folder, out var folderIssue))
                {
                    try { ValidateDirectory(folder); }
                    catch (UnauthorizedAccessException) { folderIssue = OutputIssue.UnwritableFolder; }
                    catch (Exception error) when (IsFileError(error)) { folderIssue = OutputIssue.InvalidFolder; }
                    directories.Add(folder, folderIssue);
                }

                if (folderIssue is { } issue)
                {
                    proposals.Add(new(input, null, issue));
                    continue;
                }

                proposals.Add(FindOutput(input, target, folder, policy, reserved, token));
            }

            return proposals;
        }, token);

    private string ValidateDirectory(string path)
    {
        var directory = Path.TrimEndingDirectorySeparator(PathSafety.FullPath(path));
        files.ValidateDestination(directory);
        return directory;
    }

    private OutputProposal FindOutput(InputFile input, FileFormat target, string directory, OutputCollisionPolicy policy,
        HashSet<string> reserved, CancellationToken token)
    {
        try
        {
            for (var suffix = 0; suffix <= 1000; suffix++)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, OutputNaming.CreateFileName(input.Name, target, suffix));
                if (!reserved.Contains(path) && !files.EntryExists(path))
                {
                    reserved.Add(path);
                    return new(input, path);
                }

                if (policy == OutputCollisionPolicy.Skip)
                    return new(input, null, OutputIssue.Conflict);
            }
        }
        catch (Exception error) when (IsFileError(error))
        {
            return new(input, null, OutputIssue.Unavailable);
        }

        return new(input, null, OutputIssue.Unavailable);
    }

    private static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
