using System.Security.Cryptography;
using SkiaSharp;
using UniversalConverter.Application;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;
using UniversalConverter.Infrastructure;
using Xunit;

namespace UniversalConverter.Infrastructure.Tests;

public sealed class ImageConversionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly IProgress<ConversionProgress> Progress = new Progress<ConversionProgress>();
    private readonly ImageConversionEngine _engine = new();

    [Theory]
    [InlineData("png", "jpg")]
    [InlineData("png", "webp")]
    [InlineData("jpeg", "png")]
    [InlineData("jpg", "webp")]
    [InlineData("webp", "png")]
    [InlineData("webp", "jpg")]
    public async Task ConvertsAllPairsAndReopensTheActualOutput(string source, string target)
    {
        using var workspace = new TemporaryWorkspace();
        var nested = Directory.CreateDirectory(Path.Combine(workspace.DirectoryPath, "Çalışma öğeleri")).FullName;
        var input = Fixture(nested, "Şüphe ve Çığ Iİıi." + source, source);
        var output = Path.Combine(nested, "Şüphe ve Çığ Iİıi." + target);
        var hash = Hash(input);
        var result = await Convert(input, output, target);
        Assert.Equal(output, result.OutputPath);
        using var codec = SKCodec.Create(output);
        Assert.NotNull(codec);
        Assert.Equal(Codec(target), codec.EncodedFormat);
        using var decoded = SKBitmap.Decode(output);
        Assert.Equal(96, decoded.Width);
        Assert.Equal(64, decoded.Height);
        Assert.Equal(hash, Hash(input));
        Assert.Empty(Directory.GetFiles(nested, "*.tmp"));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    public async Task EncodersUseTheSelectedQuality(string target)
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "noise.png", "png", noise: true);
        var low = Path.Combine(workspace.DirectoryPath, "low." + target);
        var high = Path.Combine(workspace.DirectoryPath, "high." + target);
        await Convert(input, low, target, quality: 5);
        await Convert(input, high, target, quality: 95);
        Assert.True(new FileInfo(high).Length > new FileInfo(low).Length);
        Assert.NotEqual(Hash(low), Hash(high));
    }

    [Fact]
    public async Task PreservesAlphaInWebpAndPngAndCompositesWhiteInJpeg()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "alpha.png", "png", transparent: true);
        var webp = Path.Combine(workspace.DirectoryPath, "alpha.webp");
        var png = Path.Combine(workspace.DirectoryPath, "lossless.png");
        var jpg = Path.Combine(workspace.DirectoryPath, "alpha.jpg");
        await Convert(input, webp, "webp");
        await Convert(webp, png, "png");
        await Convert(input, jpg, "jpg");
        using var w = SKBitmap.Decode(webp);
        using var p = SKBitmap.Decode(png);
        using var j = SKBitmap.Decode(jpg);
        Assert.Equal(0, w.GetPixel(4, 4).Alpha);
        Assert.Equal(0, p.GetPixel(4, 4).Alpha);
        Assert.Equal(w.GetPixel(80, 30), p.GetPixel(80, 30));
        Assert.InRange(w.GetPixel(80, 30).Alpha, 126, 130);
        Assert.InRange(p.GetPixel(80, 30).Alpha, 126, 130);
        var white = j.GetPixel(4, 4);
        Assert.InRange(white.Red, 245, 255);
        Assert.InRange(white.Green, 245, 255);
        Assert.InRange(white.Blue, 245, 255);
    }

    [Fact]
    public async Task RotatesExifOrientationSixAndPreservesVisualPlacement()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "orientation.jpg", "jpg");
        byte[] app1 = [
            0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0,
            73, 73, 42, 0, 8, 0, 0, 0, 1, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        var jpeg = File.ReadAllBytes(input);
        File.WriteAllBytes(input, [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)]);
        var output = Path.Combine(workspace.DirectoryPath, "orientation.png");
        using var original = SKBitmap.Decode(input);
        await Convert(input, output, "png");
        using var rotated = SKBitmap.Decode(output);
        Assert.Equal(64, rotated.Width);
        Assert.Equal(96, rotated.Height);
        Assert.Equal(original.GetPixel(10, 12), rotated.GetPixel(63 - 12, 10));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    public async Task RejectsEmptyAndDamagedInput(string source)
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.DirectoryPath, "bad." + source);
        var target = source == "png" ? "jpg" : "png";
        var output = Path.Combine(workspace.DirectoryPath, "result." + target);
        foreach (byte[] data in new byte[][] { [], [1, 2, 3, 4, 5] })
        {
            File.WriteAllBytes(input, data);
            var error = await Assert.ThrowsAsync<ConversionException>(() => Convert(input, output, target));
            Assert.Equal(ConversionFailure.InvalidImage, error.Reason);
            Assert.False(File.Exists(output));
            Assert.Equal(data, File.ReadAllBytes(input));
        }
    }

    [Fact]
    public async Task RejectsWrongExtensionAndMissingSource()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "wrong.png", "jpg");
        var output = Path.Combine(workspace.DirectoryPath, "out.webp");
        var error = await Assert.ThrowsAsync<ConversionException>(() => Convert(input, output, "webp"));
        Assert.Equal(ConversionFailure.FormatMismatch, error.Reason);
        error = await Assert.ThrowsAsync<ConversionException>(() =>
            Convert(Path.Combine(workspace.DirectoryPath, "missing.png"), output, "webp"));
        Assert.Equal(ConversionFailure.SourceAccess, error.Reason);
    }

    [Fact]
    public async Task RejectsInputOverTheByteLimitWithoutDecoding()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.DirectoryPath, "big.png");
        using (var stream = File.Create(path))
            stream.SetLength(ImageConversionEngine.MaximumInputBytes + 1);
        var error = await Assert.ThrowsAsync<ConversionException>(() =>
            Convert(path, Path.Combine(workspace.DirectoryPath, "out.jpg"), "jpg"));
        Assert.Equal(ConversionFailure.ImageTooLarge, error.Reason);
    }

    [Fact]
    public async Task ExistingOutputCanBeSkippedOrRenamedWithoutOverwrite()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "İstanbul Fotoğraf.PNG", "png");
        var output = Path.Combine(workspace.DirectoryPath, "İstanbul Fotoğraf.jpg");
        File.WriteAllText(output, "existing");
        var skipped = await Convert(input, output, "jpg", OutputCollisionPolicy.Skip);
        Assert.True(skipped.IsSkipped);
        Assert.Null(skipped.OutputPath);
        var renamed = await Convert(input, output, "jpg");
        Assert.Equal("İstanbul Fotoğraf (1).jpg", Path.GetFileName(renamed.OutputPath));
        Assert.Equal("existing", File.ReadAllText(output));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentCommitsAcquireDifferentPaths()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "shared.png", "png");
        var output = Path.Combine(workspace.DirectoryPath, "shared.jpg");
        using var gate = new Barrier(2);
        var request = Request(input, output, "jpg");
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => _engine.ConvertAsync(request, Progress, default, () =>
        {
            if (!gate.SignalAndWait(Deadline))
                throw new TimeoutException();
        }))).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(Deadline);
        Assert.Equal(2, results.Select(entry => entry.OutputPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(results, entry => Assert.True(File.Exists(entry.OutputPath)));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CancellationBeforeCommitCleansTemporaryFileAndPreservesSource()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Fixture(workspace.DirectoryPath, "original.png", "png");
        var output = Path.Combine(workspace.DirectoryPath, "out.jpg");
        var hash = Hash(source);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _engine.ConvertAsync(Request(source, output, "jpg"), Progress,
            cancellation.Token, () =>
            {
                Assert.Single(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }));
        Assert.Equal(hash, Hash(source));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task FailureBeforeCommitCleansTemporaryFileAndKeepsUserFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Fixture(workspace.DirectoryPath, "original.png", "png");
        var output = Path.Combine(workspace.DirectoryPath, "out.jpg");
        var file = Path.Combine(workspace.DirectoryPath, "user.tmp");
        File.WriteAllText(file, "user content");
        await Assert.ThrowsAsync<ConversionException>(() => _engine.ConvertAsync(Request(source, output, "jpg"), Progress,
            default, () => throw new IOException("Simulated write failure.")));
        Assert.False(File.Exists(output));
        Assert.Equal(file, Assert.Single(Directory.GetFiles(workspace.DirectoryPath, "*.tmp")));
        Assert.Equal("user content", File.ReadAllText(file));
    }

    [Fact]
    public async Task QueuePersistsRealHistoryAcrossReopenAndKeepsSimulationsSeparate()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Fixture(workspace.DirectoryPath, "Şüphe ve Çığ.jpeg", "jpg");
        var output = Path.Combine(workspace.DirectoryPath, "Şüphe ve Çığ.png");
        var db = Path.Combine(workspace.DirectoryPath, "settings.db");
        var settings = new SettingsDatabase(db);
        await settings.SaveLanguageAsync("tr-TR");
        await settings.SaveThemeAsync(ThemePreference.Dark);
        var coordinator = new ConversionCoordinator(new ConversionPlanner(new ExtensionFormatDetector(ImageFormats.All),
            new ConversionEngineRegistry([new SimulationEngine(), _engine])));
        await using (var queue = new ConversionQueueService(coordinator, history: settings))
        {
            var real = queue.Enqueue(Request(input, output, "png"));
            var fake = queue.Enqueue(new ConversionRequest(input, ImageFormats.WebP, Simulation: true));
            Assert.Equal(output, (await real.Completion.WaitAsync(Deadline)).Result?.OutputPath);
            Assert.True((await fake.Completion.WaitAsync(Deadline)).Result?.IsSimulation);
        }
        var reopened = new SettingsDatabase(db);
        var entry = Assert.Single(await reopened.LoadAsync());
        Assert.Equal(input, entry.SourcePath);
        Assert.Equal(output, entry.OutputPath);
        Assert.Equal(ConversionStatus.Completed, entry.Status);
        Assert.Equal("tr-TR", await reopened.LoadLanguageAsync());
        Assert.Equal(ThemePreference.Dark, await reopened.LoadThemeAsync());
    }

    private Task<ConversionResult> Convert(string input, string output, string target,
        OutputCollisionPolicy policy = OutputCollisionPolicy.Rename, int quality = 85) =>
        _engine.ConvertAsync(Request(input, output, target, policy, quality), Progress, default);

    private static ConversionRequest Request(string input, string output, string target,
        OutputCollisionPolicy policy = OutputCollisionPolicy.Rename, int quality = 85) =>
        new(input, new FileFormat(target, FormatCategory.Image), output, Simulation: false, CollisionPolicy: policy, Quality: quality);

    private static string Fixture(string folder, string name, string format, bool transparent = false, bool noise = false)
    {
        var path = Path.Combine(folder, name);
        using var image = new SKBitmap(96, 64);
        var random = new Random(409);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var value = noise ? random.Next(256) : (x * 2 + y) % 256;
            image.SetPixel(x, y, new SKColor((byte)value, (byte)(255 - value), (byte)(y * 3),
                (byte)(transparent ? x < 32 ? 0 : 128 : 255)));
        }
        using var bytes = image.Encode(Codec(format), 95);
        using var output = File.Create(path);
        bytes.SaveTo(output);
        return path;
    }

    private static SKEncodedImageFormat Codec(string extension) => extension switch
    {
        "png" => SKEncodedImageFormat.Png,
        "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
        _ => SKEncodedImageFormat.Webp
    };

    private static string Hash(string path) => System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
