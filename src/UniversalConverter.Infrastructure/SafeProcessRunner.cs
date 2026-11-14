using System.Diagnostics;

namespace UniversalConverter.Infrastructure;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout, Action<string>? OutputLine = null, bool RejectTruncatedOutput = false);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface ISafeProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

public sealed class SafeProcessRunner(IEnumerable<string> allowedExecutables) : ISafeProcessRunner
{
    private const int CapturedCharacters = 16_384;
    private readonly HashSet<string> _allowed = new(
        allowedExecutables.Select(Path.GetFullPath),
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Executable) || !_allowed.Contains(Path.GetFullPath(request.Executable)))
            throw new UnauthorizedAccessException("The executable is not configured.");
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            throw new ArgumentException("The working directory must exist.", nameof(request));
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(request), "The timeout must be between zero and 24 hours.");
        if (request.Arguments.Count > 128 || request.Arguments.Any(argument => argument.Length > 4096))
            throw new ArgumentException("The argument list exceeds its limits.", nameof(request));

        var start = new ProcessStartInfo(Path.GetFullPath(request.Executable))
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in request.Arguments)
            start.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process could not be started.");
        var stdout = CaptureAsync(process.StandardOutput, deadline.Token, request.OutputLine, request.RejectTruncatedOutput);
        var stderr = CaptureAsync(process.StandardError, deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited before the kill request.
            }

            if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("The configured process timeout elapsed.");
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellationToken, Action<string>? outputLine = null, bool rejectTruncated = false)
    {
        var line = new System.Text.StringBuilder();
        var truncated = false;
        var captured = new System.Text.StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            truncated |= count > CapturedCharacters - captured.Length;
            captured.Append(buffer, 0, Math.Min(count, CapturedCharacters - captured.Length));
            if (outputLine is null)
                continue;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    outputLine(line.ToString().TrimEnd('\r'));
                    line.Clear();
                }
                else if (line.Length < 512)
                    line.Append(buffer[i]);
            }
        }
        if (truncated && rejectTruncated)
            throw new IOException("Process output exceeded the capture limit.");
        return captured.ToString();
    }
}
