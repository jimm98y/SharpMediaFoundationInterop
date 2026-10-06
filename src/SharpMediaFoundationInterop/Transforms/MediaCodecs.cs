using System;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// The codecs of the system it runs on, behind <see cref="IMediaVideoTransform"/> and <see cref="IMediaAudioTransform"/>:
    /// Media Foundation's on Windows 10 1809 or later. Elsewhere there are none yet; CanDecode and CanEncode say so. A
    /// video decoder takes a coded picture in and hands NV12 out, an audio decoder PCM; an encoder the other way round.
    /// </summary>
    public static class MediaCodecs
    {
        /// <summary>
        /// Whether there is a decoder of the codec on this system. Of a codec that comes as an extension of the system's -
        /// VP9 and AV1 on Windows - whether it is installed shows only as the decoder is initialized, which then throws
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        public static bool CanDecode(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && MediaFoundationDecoder.Supports(codec);

        /// <summary>
        /// The multiple this system's decoder of the codec rounds a picture's width and height up to: the size of the frames
        /// it hands out, where the picture is cropped from.
        /// </summary>
        public static uint DecoderAlignment(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationDecoder.Alignment(codec) : 1;

        /// <summary>Whether there is an encoder of the codec on this system; as of <see cref="CanDecode"/>.</summary>
        public static bool CanEncode(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && MediaFoundationEncoder.Supports(codec);

        /// <summary>A decoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationDecoder"/>.</summary>
        public static IMediaVideoTransform CreateVideoDecoder(VideoCodec codec, VideoDecoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationDecoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} decoder on this system: there are codecs on Windows 10 1809 or later");
        }

        /// <summary>An encoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationEncoder"/>.</summary>
        public static IMediaVideoEncoder CreateVideoEncoder(VideoCodec codec, VideoEncoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationEncoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} encoder on this system: there are codecs on Windows 10 1809 or later");
        }

        /// <summary>Whether there is a decoder of the codec on this system; as of the video codecs.</summary>
        public static bool CanDecode(AudioCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && MediaFoundationAudioDecoder.Supports(codec);

        /// <summary>Whether there is an encoder of the codec on this system; as of the video codecs.</summary>
        public static bool CanEncode(AudioCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && MediaFoundationAudioEncoder.Supports(codec);

        /// <summary>A decoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationAudioDecoder"/>.</summary>
        public static IMediaAudioTransform CreateAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationAudioDecoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} decoder on this system: there are codecs on Windows 10 1809 or later");
        }

        /// <summary>An encoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationAudioEncoder"/>.</summary>
        public static IMediaAudioEncoder CreateAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationAudioEncoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} encoder on this system: there are codecs on Windows 10 1809 or later");
        }
    }
}
