using System;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// The formats a codec's <see cref="IMediaInput.InputFormat"/> and <see cref="IMediaOutput.OutputFormat"/> are told by:
    /// Media Foundation's subtypes, on every system, so they mean one thing wherever the library runs. Most are a FOURCC or
    /// a WAVE format tag in the first field of one GUID's form.
    /// </summary>
    public static class MediaFormats
    {
        public static readonly Guid NV12 = FourCC("NV12");
        public static readonly Guid RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        public static readonly Guid ARGB32 = new Guid("00000015-0000-0010-8000-00aa00389b71");
        public static readonly Guid YUY2 = FourCC("YUY2");
        public static readonly Guid UYVY = FourCC("UYVY");
        public static readonly Guid MJPG = FourCC("MJPG");

        public static readonly Guid H264 = FourCC("H264");
        public static readonly Guid HEVC = FourCC("HEVC");
        public static readonly Guid MPEG2 = new Guid("e06d8026-db46-11cf-b4d1-00805f6cbbea");
        public static readonly Guid H263 = FourCC("H263");
        public static readonly Guid MP4V = FourCC("MP4V");
        public static readonly Guid VP90 = FourCC("VP90");
        public static readonly Guid AV1 = FourCC("AV01");

        public static readonly Guid PCM = WaveFormat(0x0001);
        public static readonly Guid Float = WaveFormat(0x0003);
        public static readonly Guid AAC = WaveFormat(0x1610);
        public static readonly Guid MP3 = WaveFormat(0x0055);
        public static readonly Guid Opus = WaveFormat(0x704F);
        public static readonly Guid FLAC = WaveFormat(0xF1AC);
        public static readonly Guid ALAC = WaveFormat(0x6C61);

        public static Guid Of(VideoCodec codec) => codec switch
        {
            VideoCodec.H262 => MPEG2,
            VideoCodec.H263 => H263,
            VideoCodec.Mpeg4 => MP4V,
            VideoCodec.H264 => H264,
            VideoCodec.H265 => HEVC,
            VideoCodec.VP9 => VP90,
            VideoCodec.AV1 => AV1,
            _ => Guid.Empty
        };

        public static Guid Of(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC => AAC,
            AudioCodec.Opus => Opus,
            AudioCodec.Mp3 => MP3,
            AudioCodec.Flac => FLAC,
            AudioCodec.Alac => ALAC,
            _ => Guid.Empty
        };

        private static Guid FourCC(string code) =>
            WaveFormat((uint)(code[0] | code[1] << 8 | code[2] << 16 | code[3] << 24));

        private static Guid WaveFormat(uint tag) =>
            new Guid(tag, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    }
}
