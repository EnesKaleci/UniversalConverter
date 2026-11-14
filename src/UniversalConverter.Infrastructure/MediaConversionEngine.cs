using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Infrastructure;

public sealed class MediaConversionEngine(string toolsDirectory) : IOutputCommitEngine
{
    public string Id => "media";
    public bool IsSimulation => false;
    public IReadOnlyList<ConversionCapability> Capabilities => MediaFormats.Pairs;

    public Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
        CancellationToken token) => ConvertAsync(request, progress, token, token.ThrowIfCancellationRequested);

    public async Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
        CancellationToken token, Action beginCommit)
    {
        var source = MediaFormats.All.FirstOrDefault(x => Path.GetExtension(request.InputPath)
            .Equals("." + x.Format.Extension, StringComparison.OrdinalIgnoreCase));
        var target = MediaFormats.Find(request.Target);
        var options = request.Media ?? new();
        if (source is null || target is null || !Capabilities.Any(x => x.Matches(source.Format, target.Format)) ||
            request.OverwritePolicy != OutputOverwritePolicy.Reject || !Enum.IsDefined(options.Quality) ||
            options.Bitrate is not (128 or 192 or 256 or 320) ||
            options.Height is not (0 or 480 or 720 or 1080 or 1440 or 2160) ||
            options.FramesPerSecond is not (0 or 24 or 25 or 30 or 50 or 60))
            throw new ConversionException(ConversionFailure.InvalidMedia);

        if (!Path.IsPathFullyQualified(request.InputPath) || !Path.IsPathFullyQualified(request.OutputPath ?? "") ||
            !Path.GetExtension(request.OutputPath)!.Equals("." + target.Format.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(ConversionFailure.OutputAccess);
        var output = Path.GetFullPath(request.OutputPath!);
        var directory = Path.GetDirectoryName(output)!;
        ValidateFolder(directory);
        using var input = OpenSource(request.InputPath);
        var tools = await OpenToolsAsync(token).ConfigureAwait(false);
        using var encoderLease = tools.Encoder;
        using var probeLease = tools.Probe;
        var runner = new SafeProcessRunner([encoderLease.Name, probeLease.Name]);
        var metadata = await ProbeAsync(runner, probeLease.Name, request.InputPath, source, token).ConfigureAwait(false);
        if (source.IsVideo && metadata.Video is null)
            throw new ConversionException(ConversionFailure.InvalidMedia);
        if (!target.IsVideo && metadata.Audio is null)
            throw new ConversionException(ConversionFailure.MediaAudioMissing);

        var temp = Path.Combine(directory, $".universal-converter-{Guid.NewGuid():N}.tmp");
        var reserved = false;
        try
        {
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                reserved = true;
            var args = Arguments(request.InputPath, temp, source, target, metadata, options);
            var result = await runner.RunAsync(new(encoderLease.Name, args, toolsDirectory, TimeSpan.FromHours(2),
                line => ReportProgress(line, metadata.Duration, progress)), token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new ConversionException(result.StandardError.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
                    ? ConversionFailure.MediaEncoderUnavailable : result.StandardError.Contains("No space left", StringComparison.OrdinalIgnoreCase)
                        ? ConversionFailure.DiskFull : ConversionFailure.InvalidMedia);

            using (var written = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                written.Flush(flushToDisk: true);
            var verified = await ProbeAsync(runner, probeLease.Name, temp, target, token).ConfigureAwait(false);
            if ((target.IsVideo ? verified.Video != target.VideoCodec : verified.Video is not null) ||
                (metadata.Audio is not null && verified.Audio != target.AudioCodec) ||
                (metadata.Duration is > 0 && verified.Duration is > 0 &&
                 Math.Abs(metadata.Duration.Value - verified.Duration.Value) > Math.Max(1, metadata.Duration.Value * 0.05)))
                throw new ConversionException(ConversionFailure.InvalidMedia);

            token.ThrowIfCancellationRequested();
            ValidateFolder(directory);
            beginCommit();
            var catalog = new LocalFileCatalog();
            for (var suffix = 0; suffix <= 1000; suffix++)
            {
                var destination = suffix == 0 ? output : Path.Combine(directory,
                    OutputNaming.CreateFileName(Path.GetFileName(output), request.Target, suffix));
                try
                {
                    File.Move(temp, destination, overwrite: false);
                    reserved = false;
                    return ConversionResult.CreatedFile(destination);
                }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException)
                    && (catalog.EntryExists(destination) || Directory.Exists(destination)))
                {
                    if (request.CollisionPolicy == OutputCollisionPolicy.Skip)
                        return ConversionResult.Skipped();
                }
            }
            throw new ConversionException(ConversionFailure.OutputAccess);
        }
        catch (TimeoutException error)
        {
            throw new ConversionException(ConversionFailure.MediaTimedOut, error);
        }
        catch (Win32Exception error)
        {
            throw new ConversionException(ConversionFailure.MediaToolsMissing, error);
        }
        catch (Exception error) when (error is not ConversionException && (error is IOException or UnauthorizedAccessException))
        {
            throw new ConversionException(ConversionFailure.OutputAccess, error);
        }
        finally
        {
            if (reserved)
                File.Delete(temp);
        }
    }

    private async Task<(FileStream Encoder, FileStream Probe)> OpenToolsAsync(CancellationToken token)
    {
        FileStream? encoder = null;
        FileStream? probe = null;
        try
        {
            var manifest = new FileInfo(Path.Combine(toolsDirectory, "tools.sha256"));
            if (manifest.Length > 256)
                throw new InvalidDataException("The media tool manifest is oversized.");
            var hashes = await File.ReadAllLinesAsync(manifest.FullName, token).ConfigureAwait(false);
            if (hashes.Length != 2 || hashes.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
                throw new InvalidDataException("The media tool manifest is invalid.");
            encoder = OpenSource(Path.Combine(toolsDirectory, "ffmpeg.exe"));
            probe = OpenSource(Path.Combine(toolsDirectory, "ffprobe.exe"));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(encoder, token).ConfigureAwait(false)).Equals(hashes[0], StringComparison.OrdinalIgnoreCase) ||
                !Convert.ToHexString(await SHA256.HashDataAsync(probe, token).ConfigureAwait(false)).Equals(hashes[1], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A media tool hash does not match the configured manifest.");
            return (encoder, probe);
        }
        catch (Exception error)
        {
            encoder?.Dispose();
            probe?.Dispose();
            if (error is IOException or UnauthorizedAccessException)
                throw new ConversionException(ConversionFailure.MediaToolsMissing, error);
            throw;
        }
    }

    private static FileStream OpenSource(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(ConversionFailure.SourceAccess, error);
        }
    }

    private static void ValidateFolder(string path)
    {
        try
        {
            new LocalFileCatalog().ValidateDestination(path);
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("A linked output directory is unsupported.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(ConversionFailure.OutputAccess, error);
        }
    }

    private sealed record Metadata(string? Video, int? VideoIndex, string? Audio, int? AudioIndex, double? Duration);

    private async Task<Metadata> ProbeAsync(ISafeProcessRunner runner, string probe, string path, MediaFormat format,
        CancellationToken token)
    {
        try
        {
            var result = await runner.RunAsync(new(probe,
                ["-v", "error", "-protocol_whitelist", "file", "-f", format.Demuxer,
                    "-show_entries", "stream=index,codec_type,codec_name:stream_disposition=attached_pic:format=duration,format_name",
                    "-of", "json", "-i", path], toolsDirectory, TimeSpan.FromSeconds(30),
                RejectTruncatedOutput: true), token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new ConversionException(ConversionFailure.InvalidMedia);

            using var json = JsonDocument.Parse(result.StandardOutput);
            var root = json.RootElement;
            string? video = null, audio = null;
            int? videoIndex = null, audioIndex = null;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_name", out var codec) || !stream.TryGetProperty("index", out var index))
                    continue;
                var type = stream.GetProperty("codec_type").GetString();
                if (type == "video" && video is null &&
                    !(stream.TryGetProperty("disposition", out var flags) &&
                      flags.TryGetProperty("attached_pic", out var cover) && cover.GetInt32() == 1))
                {
                    video = codec.GetString();
                    videoIndex = index.GetInt32();
                }
                if (type == "audio" && audio is null)
                {
                    audio = codec.GetString();
                    audioIndex = index.GetInt32();
                }
            }
            double? duration = root.TryGetProperty("format", out var info) &&
                info.TryGetProperty("duration", out var value) &&
                double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var seconds) &&
                double.IsFinite(seconds) && seconds > 0 ? seconds : null;
            if (video is null && audio is null)
                throw new ConversionException(ConversionFailure.InvalidMedia);
            return new(video, videoIndex, audio, audioIndex, duration);
        }
        catch (TimeoutException error) { throw new ConversionException(ConversionFailure.MediaTimedOut, error); }
        catch (Win32Exception error) { throw new ConversionException(ConversionFailure.MediaToolsMissing, error); }
        catch (Exception error) when (error is not ConversionException &&
            (error is JsonException or InvalidOperationException or KeyNotFoundException or IOException))
        { throw new ConversionException(ConversionFailure.InvalidMedia, error); }
    }

    private static List<string> Arguments(string input, string output, MediaFormat source, MediaFormat target,
        Metadata metadata, MediaOptions options)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y",
            "-progress", "pipe:1", "-protocol_whitelist", "file", "-f", source.Demuxer, "-i", input,
            "-map_metadata", "-1", "-map_chapters", "-1" };
        if (target.IsVideo)
        {
            args.AddRange(["-map", $"0:{metadata.VideoIndex}", "-c:v", target.VideoEncoder!, "-pix_fmt", "yuv420p"]);
            var crf = (target.VideoCodec, options.Quality) switch
            {
                ("vp9", MediaQuality.Low) => "38", ("vp9", MediaQuality.High) => "25", ("vp9", _) => "32",
                (_, MediaQuality.Low) => "29", (_, MediaQuality.High) => "18", _ => "23"
            };
            args.AddRange(target.VideoCodec == "vp9" ? ["-b:v", "0", "-crf", crf, "-deadline", "good", "-cpu-used", "4"]
                : ["-crf", crf, "-preset", "medium"]);
            if (options.Height > 0)
                args.AddRange(["-vf", FormattableString.Invariant($"scale=-2:trunc(min(ih\\,{options.Height})/2)*2")]);
            else
                args.AddRange(["-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2"]);
            if (options.FramesPerSecond > 0)
                args.AddRange(["-r", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture)]);
        }
        if (metadata.AudioIndex is { } audio)
        {
            args.AddRange(["-map", $"0:{audio}", "-c:a", target.AudioEncoder, "-ac", "2", "-ar", "48000"]);
            if (!target.IsLosslessAudio)
            {
                var rate = options.Quality switch
                {
                    MediaQuality.Low => 128, MediaQuality.High => 256, MediaQuality.Custom => options.Bitrate, _ => 192
                };
                args.AddRange(["-b:a", FormattableString.Invariant($"{rate}k")]);
            }
        }
        args.AddRange(["-f", target.Muxer, output]);
        return args;
    }

    private static void ReportProgress(string line, double? duration, IProgress<ConversionProgress> progress)
    {
        const string prefix = "out_time_us=";
        if (duration is > 0 && line.StartsWith(prefix, StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(prefix.Length), CultureInfo.InvariantCulture, out var micros) && micros >= 0)
            progress.Report(new((int)Math.Clamp(micros / 10000d / duration.Value, 0, 99)));
    }
}
