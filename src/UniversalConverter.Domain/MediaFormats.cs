namespace UniversalConverter.Domain;

public enum MediaQuality { Low, Standard, High, Custom }

public sealed record MediaOptions(MediaQuality Quality = MediaQuality.Standard, int Bitrate = 192,
    int Height = 0, int FramesPerSecond = 0);

public sealed record MediaFormat(FileFormat Format, string Demuxer, string Muxer, string AudioEncoder,
    string AudioCodec, string? VideoEncoder = null, string? VideoCodec = null)
{
    public bool IsVideo => VideoEncoder is not null;
    public bool IsLosslessAudio => AudioCodec is "flac" or "pcm_s16le";
}

public static class MediaFormats
{
    public static IReadOnlyList<MediaFormat> All { get; } =
    [
        Audio("mp3", "mp3", "mp3", "libmp3lame", "mp3"),
        Audio("wav", "wav", "wav", "pcm_s16le", "pcm_s16le"),
        Audio("flac", "flac", "flac", "flac", "flac"),
        Audio("aac", "aac", "adts", "aac", "aac"),
        Audio("m4a", "mov", "ipod", "aac", "aac"),
        Audio("ogg", "ogg", "ogg", "libvorbis", "vorbis"),
        Audio("opus", "ogg", "opus", "libopus", "opus"),
        Video("mp4", "mov", "mp4", "aac", "aac", "libx264", "h264"),
        Video("mkv", "matroska", "matroska", "aac", "aac", "libx264", "h264"),
        Video("webm", "matroska", "webm", "libopus", "opus", "libvpx-vp9", "vp9"),
        Video("mov", "mov", "mov", "aac", "aac", "libx264", "h264"),
        Video("avi", "avi", "avi", "libmp3lame", "mp3", "libx264", "h264")
    ];

    public static IReadOnlyList<ConversionCapability> Pairs { get; } =
        (from source in All from target in All
         where source.Format != target.Format && (source.IsVideo || !target.IsVideo)
         select new ConversionCapability(source.Format, target.Format)).ToArray();

    public static MediaFormat? Find(FileFormat format) => All.FirstOrDefault(item => item.Format == format);

    private static MediaFormat Audio(string ext, string demux, string mux, string encoder, string codec) =>
        new(new FileFormat(ext, FormatCategory.Audio), demux, mux, encoder, codec);

    private static MediaFormat Video(string ext, string demux, string mux, string audioEncoder, string audioCodec,
        string videoEncoder, string videoCodec) =>
        new(new FileFormat(ext, FormatCategory.Video), demux, mux, audioEncoder, audioCodec, videoEncoder, videoCodec);
}
