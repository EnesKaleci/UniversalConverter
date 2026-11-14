using System.Security.Cryptography;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class MediaConversionTests
{
    [Theory]
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
        Assert.SkipWhen(string.IsNullOrEmpty(tools), "Pinned FFmpeg integration runs in the Windows release profile.");
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
