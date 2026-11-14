using System.Buffers.Binary;
using SkiaSharp;
using UniversalConverter.Contracts;
using UniversalConverter.Domain;

namespace UniversalConverter.Infrastructure;

public sealed class ImageConversionEngine : IOutputCommitEngine
{
    public const long MaximumInputBytes = 64 * 1024 * 1024L;
    public const long MaximumPixels = 20_000_000;
    public const int MaximumDimension = 16_384;

    public string Id => "image";
    public bool IsSimulation => false;
    public IReadOnlyList<ConversionCapability> Capabilities => ImageFormats.Pairs;

    public Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
        CancellationToken token) => ConvertAsync(request, progress, token, token.ThrowIfCancellationRequested);

    public Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress> progress,
        CancellationToken token, Action beginCommit)
    {
        token.ThrowIfCancellationRequested();
        if (request.OverwritePolicy != OutputOverwritePolicy.Reject || request.Quality is < 1 or > 100 ||
            !TryGetFormat(request.InputPath, out var expected) || !TryGetFormat(request.OutputPath, out var target) ||
            expected == target || target != EncodedFormat(request.Target))
            throw new ConversionException(ConversionFailure.FormatMismatch);

        using var input = OpenSource(request.InputPath);
        if (input.Length > MaximumInputBytes)
            throw new ConversionException(ConversionFailure.ImageTooLarge);

        using var stream = new SKManagedStream(input, false);
        using var codec = SKCodec.Create(stream) ?? throw new ConversionException(ConversionFailure.InvalidImage);
        if (codec.EncodedFormat != expected)
            throw new ConversionException(ConversionFailure.FormatMismatch);
        CheckDimensions(codec.Info);
        if (codec.FrameCount > 1 || HasAnimationMarker(input, expected, token))
            throw new ConversionException(ConversionFailure.AnimatedImage);
        token.ThrowIfCancellationRequested();

        // Reading headers directly moved the file position. A fresh codec owns decoding from the start.
        input.Position = 0;
        using var decodeStream = new SKManagedStream(input, false);
        using var decoder = SKCodec.Create(decodeStream) ?? throw new ConversionException(ConversionFailure.InvalidImage);
        using var colorSpace = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, colorSpace);
        using var bitmap = new SKBitmap(info);
        var pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero)
            throw new ConversionException(ConversionFailure.ImageTooLarge);
        if (decoder.GetPixels(info, pixels) != SKCodecResult.Success)
            throw new ConversionException(ConversionFailure.InvalidImage);
        token.ThrowIfCancellationRequested();

        using var transformed = Orient(bitmap, codec.EncodedOrigin, target == SKEncodedImageFormat.Jpeg);
        var image = transformed ?? bitmap;
        return Task.FromResult(WriteOutput(request, image, target, token, beginCommit));
    }

    private static ConversionResult WriteOutput(ConversionRequest request, SKBitmap bitmap, SKEncodedImageFormat format,
        CancellationToken token, Action beginCommit)
    {
        var output = Path.GetFullPath(request.OutputPath!);
        var directory = Path.GetDirectoryName(output)!;
        var path = Path.Combine(directory, $".universal-converter-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            ValidateDirectory(directory);
            token.ThrowIfCancellationRequested();
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var pixmap = bitmap.PeekPixels();
                // Encoding to SKData keeps disk I/O exceptions on the managed side of the native boundary.
                using var encoded = format == SKEncodedImageFormat.Webp
                    ? pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, request.Quality))
                    : pixmap.Encode(format, request.Quality);
                if (encoded is null || encoded.Size == 0)
                    throw new ConversionException(ConversionFailure.Encoding);
                token.ThrowIfCancellationRequested();
                using var bytes = encoded.AsStream();
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = bytes.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    file.Write(buffer, 0, count);
                }
                file.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            using (var verification = File.OpenRead(path))
            using (var stream = new SKManagedStream(verification, false))
            using (var codec = SKCodec.Create(stream))
            {
                if (codec is null || codec.EncodedFormat != format ||
                    codec.Info.Width != bitmap.Width || codec.Info.Height != bitmap.Height ||
                    codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                    throw new ConversionException(ConversionFailure.Encoding);
            }
            token.ThrowIfCancellationRequested();
            ValidateDirectory(directory);
            beginCommit();

            var filesystem = new LocalFileCatalog();
            var policy = request.CollisionPolicy ?? OutputCollisionPolicy.Skip;
            for (var suffix = 0; suffix <= 1000; suffix++)
            {
                var candidate = suffix == 0 ? output : Path.Combine(directory,
                    OutputNaming.CreateFileName(Path.GetFileName(output), request.Target, suffix));
                try
                {
                    File.Move(path, candidate, overwrite: false);
                    created = false;
                    return ConversionResult.CreatedFile(candidate);
                }
                catch (Exception error) when ((error is IOException or UnauthorizedAccessException) &&
                                              (filesystem.EntryExists(candidate) || Directory.Exists(candidate)))
                {
                    if (policy == OutputCollisionPolicy.Skip)
                        return ConversionResult.Skipped();
                }
            }
            throw new ConversionException(ConversionFailure.OutputAccess);
        }
        catch (Exception error) when (error is not ConversionException && (error is IOException or UnauthorizedAccessException))
        {
            var code = error.HResult & 0xffff;
            throw new ConversionException(code is 39 or 112 or 28 ? ConversionFailure.DiskFull : ConversionFailure.OutputAccess, error);
        }
        finally
        {
            if (created)
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new ConversionException(ConversionFailure.OutputAccess, error);
                }
            }
        }
    }

    private static void ValidateDirectory(string path)
    {
        new LocalFileCatalog().ValidateDestination(path);
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || directory.LinkTarget is not null)
                throw new ConversionException(ConversionFailure.OutputAccess);
    }

    private static FileStream OpenSource(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(ConversionFailure.SourceAccess, error);
        }
    }

    private static void CheckDimensions(SKImageInfo info)
    {
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaximumDimension || info.Height > MaximumDimension ||
            (long)info.Width * info.Height > MaximumPixels)
            throw new ConversionException(ConversionFailure.ImageTooLarge);
    }

    private static SKEncodedImageFormat? EncodedFormat(FileFormat format) =>
        TryGetFormat("." + format.Extension, out var encoded) ? encoded : null;

    private static bool TryGetFormat(string? path, out SKEncodedImageFormat format)
    {
        var extension = Path.GetExtension(path)?.ToLowerInvariant();
        format = extension switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => default
        };
        return extension is ".png" or ".jpg" or ".jpeg" or ".webp";
    }

    private static bool HasAnimationMarker(Stream stream, SKEncodedImageFormat format, CancellationToken token)
    {
        if (format == SKEncodedImageFormat.Webp)
        {
            Span<byte> header = stackalloc byte[21];
            stream.Position = 0;
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length &&
                header[12..16].SequenceEqual("VP8X"u8) && (header[20] & 2) != 0;
        }
        if (format != SKEncodedImageFormat.Png)
            return false;

        stream.Position = 8;
        Span<byte> chunk = stackalloc byte[8];
        while (stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) == chunk.Length)
        {
            token.ThrowIfCancellationRequested();
            if (chunk[4..].SequenceEqual("acTL"u8))
                return true;
            if (chunk[4..].SequenceEqual("IDAT"u8))
                return false;
            var length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
            if ((long)length + 4 > stream.Length - stream.Position)
                break;
            stream.Position += (long)length + 4;
        }
        return false;
    }

    private static SKBitmap? Orient(SKBitmap image, SKEncodedOrigin origin, bool opaque)
    {
        if (!opaque && origin == SKEncodedOrigin.TopLeft)
            return null;

        var width = image.Width;
        var height = image.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var info = new SKImageInfo(swap ? height : width, swap ? width : height, image.ColorType,
            opaque ? SKAlphaType.Opaque : SKAlphaType.Premul, image.Info.ColorSpace);
        var (sx, kx, tx, ky, sy, ty) = origin switch
        {
            SKEncodedOrigin.TopRight => (-1, 0, width, 0, 1, 0),
            SKEncodedOrigin.BottomRight => (-1, 0, width, 0, -1, height),
            SKEncodedOrigin.BottomLeft => (1, 0, 0, 0, -1, height),
            SKEncodedOrigin.LeftTop => (0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => (0, -1, height, 1, 0, 0),
            SKEncodedOrigin.RightBottom => (0, -1, height, -1, 0, width),
            SKEncodedOrigin.LeftBottom => (0, 1, 0, -1, 0, width),
            _ => (1, 0, 0, 0, 1, 0)
        };
        var bitmap = new SKBitmap(info);
        try
        {
            if (bitmap.GetPixels() == IntPtr.Zero)
                throw new ConversionException(ConversionFailure.ImageTooLarge);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
            canvas.SetMatrix(new SKMatrix(sx, kx, tx, ky, sy, ty, 0, 0, 1));
            canvas.DrawBitmap(image, 0, 0);
            canvas.Flush();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
