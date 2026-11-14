using System.Security.Cryptography;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class PinnedMediaTheoryAttribute : TheoryAttribute
{
    public PinnedMediaTheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONVERTER_TEST_MEDIA_TOOLS")))
            Skip = "Pinned FFmpeg integration runs in the Windows release profile.";
    }
}

public sealed class MediaConversionTests
{
    [PinnedMediaTheory]
    [InlineData("wav", "mp3")]
    [InlineData("mp3", "wav")]
    [InlineData("wav", "flac")]
    [InlineData("flac", "mp3")]
    [InlineData("wav", "aac")]
    [InlineData("aac", "m4a")]
    [InlineData("wav", "ogg")]
    [InlineData("ogg", "opus")]
    [InlineData("mp4", "mkv")]
    [InlineData("mkv", "mp4")]
    [InlineData("mp4", "webm")]
    [InlineData("webm", "mp4")]
    [InlineData("mp4", "mp3")]
    [InlineData("mp4", "wav")]
    [InlineData("mp4", "aac")]
    [InlineData("mp4", "m4a")]
    [InlineData("mkv", "mp3")]
    [InlineData("mkv", "flac")]
    [InlineData("webm", "opus")]
    [InlineData("mp4", "mov")]
    [InlineData("mov", "avi")]
    public async Task ConvertsAndVerifiesMediaWhenPinnedToolsAreInstalled(string source, string target)
    {
        var tools = Environment.GetEnvironmentVariable("CONVERTER_TEST_MEDIA_TOOLS");

        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.DirectoryPath, $"İstanbul Şarkı Iİıi.{source}");
        var sourceFormat = MediaFormats.All.Single(x => x.Format.Extension == source);
        var targetFormat = MediaFormats.All.Single(x => x.Format.Extension == target);
        var ffmpeg = Path.Combine(tools!, "ffmpeg.exe");
        var ffprobe = Path.Combine(tools!, "ffprobe.exe");
        var runner = new SafeProcessRunner([ffmpeg, ffprobe]);
        var args = new List<string> { "-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440:duration=1" };
        if (sourceFormat.IsVideo)
            args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=64x48:rate=12:duration=1", "-c:v", sourceFormat.VideoEncoder!, "-shortest"]);
        args.AddRange(["-c:a", sourceFormat.AudioEncoder, "-f", sourceFormat.Muxer, input]);
        var fixture = await runner.RunAsync(new(ffmpeg, args, workspace.DirectoryPath, TimeSpan.FromSeconds(30)));
        Assert.True(fixture.ExitCode == 0, fixture.StandardError);
        var hash = SHA256.HashData(File.ReadAllBytes(input));
        var output = Path.Combine(workspace.DirectoryPath, $"Çığ ve Işık.{target}");
        var result = await new MediaConversionEngine(tools!).ConvertAsync(
            new(input, targetFormat.Format, output, Simulation: false, CollisionPolicy: OutputCollisionPolicy.Rename),
            new Progress<ConversionProgress>(), CancellationToken.None);
        Assert.Equal(output, result.OutputPath);
        Assert.True(new FileInfo(output).Length > 0);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [PinnedMediaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentVideoReportsMissingAudioOrStaysSilent(bool keepVideo)
    {
        var tools = Environment.GetEnvironmentVariable("CONVERTER_TEST_MEDIA_TOOLS")!;
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.DirectoryPath, "Şüphe Video.mp4");
        var exe = Path.Combine(tools, "ffmpeg.exe");
        var runner = new SafeProcessRunner([exe]);
        var fixture = await runner.RunAsync(new(exe, ["-v", "error", "-nostdin", "-f", "lavfi", "-i",
            "testsrc2=size=64x48:rate=12:duration=1", "-c:v", "libx264", source],
            workspace.DirectoryPath, TimeSpan.FromSeconds(30)));
        Assert.True(fixture.ExitCode == 0, fixture.StandardError);
        var engine = new MediaConversionEngine(tools);
        var mp4 = MediaFormats.All.Single(format => format.Format.Extension == "mp4");
        var sourceInfo = await engine.InspectAsync(source, mp4.Format, default);
        Assert.Equal("h264", sourceInfo.Video);
        Assert.Equal(64, sourceInfo.Width);
        Assert.Null(sourceInfo.Audio);
        var target = MediaFormats.All.Single(format => format.Format.Extension == (keepVideo ? "webm" : "wav"));
        var output = Path.ChangeExtension(source, target.Format.Extension);
        var request = new ConversionRequest(source, target.Format, output, CollisionPolicy: OutputCollisionPolicy.Rename);
        var progress = new Progress<ConversionProgress>();
        if (keepVideo)
        {
            await engine.ConvertAsync(request, progress, default);
            var info = await engine.InspectAsync(output, target.Format, default);
            Assert.Equal("vp9", info.Video);
            Assert.Null(info.Audio);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConversionException>(() => engine.ConvertAsync(request, progress, default));
            Assert.Equal(ConversionFailure.MediaAudioMissing, error.Reason);
            Assert.False(File.Exists(output));
        }
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [PinnedMediaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitCancellationAndCollisionLeaveNoTemporaryFile(bool cancel)
    {
        var tools = Environment.GetEnvironmentVariable("CONVERTER_TEST_MEDIA_TOOLS")!;
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.DirectoryPath, "Çığ.wav");
        var output = Path.ChangeExtension(input, ".mp3");
        var exe = Path.Combine(tools, "ffmpeg.exe");
        var fixture = await new SafeProcessRunner([exe]).RunAsync(new(exe,
            ["-v", "error", "-f", "lavfi", "-i", "sine=duration=1", input],
            workspace.DirectoryPath, TimeSpan.FromSeconds(30)));
        Assert.True(fixture.ExitCode == 0, fixture.StandardError);
        var target = MediaFormats.All.Single(format => format.Format.Extension == "mp3").Format;
        var request = new ConversionRequest(input, target, output, CollisionPolicy: OutputCollisionPolicy.Skip);
        var engine = new MediaConversionEngine(tools);
        using var stop = new CancellationTokenSource();
        Task<ConversionResult> Run() => engine.ConvertAsync(request, new Progress<ConversionProgress>(), stop.Token, () =>
        {
            if (cancel)
                stop.Cancel();
            else
                File.WriteAllText(output, "Existing file");
            stop.Token.ThrowIfCancellationRequested();
        });
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            Assert.False(File.Exists(output));
        }
        else
        {
            Assert.True((await Run()).IsSkipped);
            Assert.Equal("Existing file", File.ReadAllText(output));
        }
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void MediaCatalogRejectsInvalidPairsAndUsesExplicitMuxers()
    {
        Assert.DoesNotContain(MediaFormats.Pairs, pair => pair.Source == pair.Target);
        Assert.DoesNotContain(MediaFormats.Pairs, pair => pair.Source.Category == FormatCategory.Audio &&
                                                        pair.Target.Category == FormatCategory.Video);
        var opus = MediaFormats.All.Single(format => format.Format.Extension == "opus");
        var aac = MediaFormats.All.Single(format => format.Format.Extension == "aac");
        Assert.Equal("ogg", opus.Demuxer);
        Assert.Equal("opus", opus.Muxer);
        Assert.Equal("adts", aac.Muxer);
    }

    [Fact]
    public async Task MissingToolsAreReportedBeforeCreatingAnOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.DirectoryPath, "İstanbul.wav");
        var output = Path.Combine(workspace.DirectoryPath, "İstanbul.mp3");
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var target = MediaFormats.All.Single(x => x.Format.Extension == "mp3").Format;
        var error = await Assert.ThrowsAsync<ConversionException>(() => new MediaConversionEngine(workspace.DirectoryPath)
            .ConvertAsync(new(input, target, output), new Progress<ConversionProgress>(), CancellationToken.None));
        Assert.Equal(ConversionFailure.MediaToolsMissing, error.Reason);
        Assert.False(File.Exists(output));
    }
}
