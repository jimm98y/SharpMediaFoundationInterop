using System;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// The codecs of the system it runs on, behind <see cref="IMediaVideoTransform"/> and <see cref="IMediaAudioTransform"/>:
    /// Media Foundation's on Windows 10 1809 or later; VideoToolbox's and AudioToolbox's on macOS 11 and iOS 14 or later;
    /// MediaCodec's on Android 8 or later; GStreamer's on Linux, of the plugins installed. Elsewhere there are none; CanDecode and CanEncode say so. A video decoder takes a coded picture in and hands NV12 out, an
    /// audio decoder PCM; an encoder the other way round.
    /// </summary>
    public static class MediaCodecs
    {
        /// <summary>macOS 11 or later, or iOS 14 or later: of VideoToolbox's and AudioToolbox's codecs alike.</summary>
        [SupportedOSPlatformGuard("macos11.0")]
        [SupportedOSPlatformGuard("ios14.0")]
        private static bool IsApple => OperatingSystem.IsMacOSVersionAtLeast(11) || OperatingSystem.IsIOSVersionAtLeast(14);

        /// <summary>Android 8 or later: of MediaCodec, through the NDK. Asked before Linux, which an Android process counts as.</summary>
        [SupportedOSPlatformGuard("android26.0")]
        private static bool IsAndroid => OperatingSystem.IsAndroidVersionAtLeast(26);

        /// <summary>
        /// Whether there is a decoder of the codec on this system. Of a codec that comes as an extension of the system's -
        /// VP9 and AV1 on Windows - whether it is installed shows only as the decoder is initialized, which then throws
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        public static bool CanDecode(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationDecoder.Supports(codec) :
            IsApple ? VideoToolboxDecoder.Supports(codec) :
            IsAndroid ? AndroidVideoDecoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerVideoDecoder.Supports(codec);

        /// <summary>
        /// The multiple this system's decoder of the codec rounds a picture's width and height up to: the size of the frames
        /// it hands out, where the picture is cropped from.
        /// </summary>
        public static uint DecoderAlignment(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationDecoder.Alignment(codec) :
            IsApple ? VideoToolboxDecoder.ResMultiple :
            IsAndroid ? AndroidVideoDecoder.ResMultiple :
            OperatingSystem.IsLinux() ? GStreamerVideoDecoder.ResMultiple : 1;

        /// <summary>Whether there is an encoder of the codec on this system; as of <see cref="CanDecode"/>.</summary>
        public static bool CanEncode(VideoCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationEncoder.Supports(codec) :
            IsApple ? VideoToolboxEncoder.Supports(codec) :
            IsAndroid ? AndroidVideoEncoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerVideoEncoder.Supports(codec);

        /// <summary>
        /// A decoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationDecoder"/>, on macOS a
        /// <see cref="VideoToolboxDecoder"/>, on Android an <see cref="AndroidVideoDecoder"/>, on Linux a <see cref="GStreamerVideoDecoder"/>.
        /// </summary>
        public static IMediaVideoTransform CreateVideoDecoder(VideoCodec codec, VideoDecoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationDecoder(codec, options);
            if (IsApple)
                return new VideoToolboxDecoder(codec, options);
            if (IsAndroid)
                return new AndroidVideoDecoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerVideoDecoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} decoder on this system: {Systems}");
        }

        /// <summary>
        /// An encoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationEncoder"/>, on macOS a
        /// <see cref="VideoToolboxEncoder"/>, on Android an <see cref="AndroidVideoEncoder"/>, on Linux a <see cref="GStreamerVideoEncoder"/>.
        /// </summary>
        public static IMediaVideoEncoder CreateVideoEncoder(VideoCodec codec, VideoEncoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationEncoder(codec, options);
            if (IsApple)
                return new VideoToolboxEncoder(codec, options);
            if (IsAndroid)
                return new AndroidVideoEncoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerVideoEncoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} encoder on this system: {Systems}");
        }

        /// <summary>Whether there is a decoder of the codec on this system; as of the video codecs.</summary>
        public static bool CanDecode(AudioCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationAudioDecoder.Supports(codec) :
            IsApple ? AudioToolboxDecoder.Supports(codec) :
            IsAndroid ? AndroidAudioDecoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerAudioDecoder.Supports(codec);

        /// <summary>Whether there is an encoder of the codec on this system; as of the video codecs.</summary>
        public static bool CanEncode(AudioCodec codec) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? MediaFoundationAudioEncoder.Supports(codec) :
            IsApple ? AudioToolboxEncoder.Supports(codec) :
            IsAndroid ? AndroidAudioEncoder.Supports(codec) :
            OperatingSystem.IsLinux() && GStreamerAudioEncoder.Supports(codec);

        /// <summary>
        /// A decoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationAudioDecoder"/>, on macOS an
        /// <see cref="AudioToolboxDecoder"/>, on Android an <see cref="AndroidAudioDecoder"/>, on Linux a <see cref="GStreamerAudioDecoder"/>.
        /// </summary>
        public static IMediaAudioTransform CreateAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationAudioDecoder(codec, options);
            if (IsApple)
                return new AudioToolboxDecoder(codec, options);
            if (IsAndroid)
                return new AndroidAudioDecoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerAudioDecoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} decoder on this system: {Systems}");
        }

        /// <summary>
        /// An encoder of the codec, to be initialized; on Windows a <see cref="MediaFoundationAudioEncoder"/>, on macOS an
        /// <see cref="AudioToolboxEncoder"/>, on Android an <see cref="AndroidAudioEncoder"/>, on Linux a <see cref="GStreamerAudioEncoder"/>.
        /// </summary>
        public static IMediaAudioEncoder CreateAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                return new MediaFoundationAudioEncoder(codec, options);
            if (IsApple)
                return new AudioToolboxEncoder(codec, options);
            if (IsAndroid)
                return new AndroidAudioEncoder(codec, options);
            if (OperatingSystem.IsLinux())
                return new GStreamerAudioEncoder(codec, options);

            throw new PlatformNotSupportedException($"No {codec} encoder on this system: {Systems}");
        }

        private const string Systems = "there are codecs on Windows 10 1809 or later, on macOS 11 and iOS 14 or later, on Android 8 or later, and on Linux of GStreamer";
    }
}
