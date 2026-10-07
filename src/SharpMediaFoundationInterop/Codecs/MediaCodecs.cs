using System;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// The codecs of the system it runs on, behind <see cref="IMediaVideoTransform"/> and <see cref="IMediaAudioTransform"/>:
    /// Media Foundation's on Windows 10 1809 or later; VideoToolbox's and AudioToolbox's on macOS 11 or later; GStreamer's on
    /// Linux, of the plugins installed. Elsewhere there are none; CanDecode and CanEncode say so. A video decoder takes a coded picture in and hands NV12 out, an
    /// audio decoder PCM; an encoder the other way round.
    /// </summary>
    public static class MediaCodecs
    {
        /// <summary>
        /// Whether there is a decoder of the codec on this system. Of a codec that comes as an extension of the system's -
        /// VP9 and AV1 on Windows - whether it is installed shows only as the decoder is initialized, which then throws
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        public static bool CanDecode(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationDecoder.Supports(codec) :
            OperatingSystem.IsMacOSVersionAtLeast(11) ? VideoToolboxDecoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerVideoDecoder.Supports(codec);

        /// <summary>
        /// The multiple this system's decoder of the codec rounds a picture's width and height up to: the size of the frames
        /// it hands out, where the picture is cropped from.
        /// </summary>
        public static uint DecoderAlignment(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationDecoder.Alignment(codec) :
            OperatingSystem.IsMacOSVersionAtLeast(11) ? VideoToolboxDecoder.ResMultiple :
            OperatingSystem.IsLinux() ? GStreamerVideoDecoder.ResMultiple : 1;

        /// <summary>Whether there is an encoder of the codec on this system; as of <see cref="CanDecode"/>.</summary>
        public static bool CanEncode(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationEncoder.Supports(codec) :
            OperatingSystem.IsMacOSVersionAtLeast(11) ? VideoToolboxEncoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerVideoEncoder.Supports(codec);

        /// <summary>
        /// A decoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationDecoder"/>, on macOS a
        /// <see cref="VideoToolboxDecoder"/>, on Linux a <see cref="GStreamerVideoDecoder"/>.
        /// </summary>
        public static IMediaVideoTransform CreateVideoDecoder(VideoCodec codec, VideoDecoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationDecoder(codec, options);
            if (OperatingSystem.IsMacOSVersionAtLeast(11))
                return new VideoToolboxDecoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerVideoDecoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} decoder on this system: {Systems}");
        }

        /// <summary>
        /// An encoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationEncoder"/>, on macOS a
        /// <see cref="VideoToolboxEncoder"/>, on Linux a <see cref="GStreamerVideoEncoder"/>.
        /// </summary>
        public static IMediaVideoEncoder CreateVideoEncoder(VideoCodec codec, VideoEncoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationEncoder(codec, options);
            if (OperatingSystem.IsMacOSVersionAtLeast(11))
                return new VideoToolboxEncoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerVideoEncoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} encoder on this system: {Systems}");
        }

        /// <summary>Whether there is a decoder of the codec on this system; as of the video codecs.</summary>
        public static bool CanDecode(AudioCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationAudioDecoder.Supports(codec) :
            OperatingSystem.IsMacOSVersionAtLeast(11) ? AudioToolboxDecoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerAudioDecoder.Supports(codec);

        /// <summary>Whether there is an encoder of the codec on this system; as of the video codecs.</summary>
        public static bool CanEncode(AudioCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationAudioEncoder.Supports(codec) :
            OperatingSystem.IsMacOSVersionAtLeast(11) ? AudioToolboxEncoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerAudioEncoder.Supports(codec);

        /// <summary>
        /// A decoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationAudioDecoder"/>, on macOS an
        /// <see cref="AudioToolboxDecoder"/>, on Linux a <see cref="GStreamerAudioDecoder"/>.
        /// </summary>
        public static IMediaAudioTransform CreateAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationAudioDecoder(codec, options);
            if (OperatingSystem.IsMacOSVersionAtLeast(11))
                return new AudioToolboxDecoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerAudioDecoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} decoder on this system: {Systems}");
        }

        /// <summary>
        /// An encoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationAudioEncoder"/>, on macOS an
        /// <see cref="AudioToolboxEncoder"/>, on Linux a <see cref="GStreamerAudioEncoder"/>.
        /// </summary>
        public static IMediaAudioEncoder CreateAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationAudioEncoder(codec, options);
            if (OperatingSystem.IsMacOSVersionAtLeast(11))
                return new AudioToolboxEncoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerAudioEncoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} encoder on this system: {Systems}");
        }

        private const string Systems = "there are codecs on Windows 10 1809 or later, on macOS 11 or later, and on Linux of GStreamer";
    }
}
