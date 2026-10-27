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
            if (options != null && options.OutputFormat != VideoFrameFormat.NV12)
                throw new NotSupportedException($"No {codec} decoder of {options.OutputFormat} on this system: {Systems}");
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
            codec == AudioCodec.Pcm ? true :
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
        /// A decoder of the codec, to be initialized; of PCM a <see cref="PcmDecoder"/>, of every platform; else on Windows a
        /// <see cref="MediaFoundationAudioDecoder"/>, on macOS an
        /// <see cref="AudioToolboxDecoder"/>, on Android an <see cref="AndroidAudioDecoder"/>, on Linux a <see cref="GStreamerAudioDecoder"/>.
        /// </summary>
        public static IMediaAudioTransform CreateAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            // uncompressed PCM is converted here, of every platform: no decoder of its own is needed
            if (codec == AudioCodec.Pcm)
                return new PcmDecoder(options);
            // Opus of more than two channels, or streams, is split here into its streams, each a platform decoder's
            if (codec == AudioCodec.Opus && OpusMultistreamDecoder.IsMultistream(options))
                return new OpusMultistreamDecoder(options);
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

        /// <summary>iOS 16 or later, or macOS 11 or later: of VideoToolbox's pixel transfer.</summary>
        [SupportedOSPlatformGuard("macos11.0")]
        [SupportedOSPlatformGuard("ios16.0")]
        private static bool HasPixelTransfer => OperatingSystem.IsMacOSVersionAtLeast(11) || OperatingSystem.IsIOSVersionAtLeast(16);

        /// <summary>
        /// Whether there is a color converter of the one format to the other here: of the system's, or of managed code
        /// where the system has none. Of NV12, YUY2, UYVY, RGB24, RGB32 and ARGB32 - <see cref="MediaFormats"/>.
        /// </summary>
        public static bool CanConvert(Guid inputFormat, Guid outputFormat, ColorConverterOptions options = null) =>
            ColorFormats.IsKnown(inputFormat) && ColorFormats.IsKnown(outputFormat) &&
            (HasNativeConverter(inputFormat, outputFormat, options ?? new ColorConverterOptions()) || ManagedColorConverter.Supports(inputFormat, outputFormat));

        private static bool HasNativeConverter(Guid inputFormat, Guid outputFormat, ColorConverterOptions options) =>
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && ColorConverter.Supports(inputFormat, outputFormat, options) ||
            HasPixelTransfer && VideoToolboxColorConverter.Supports(inputFormat, outputFormat, options) ||
            !IsAndroid && OperatingSystem.IsLinux() && GStreamerColorConverter.Supports(inputFormat, outputFormat);

        /// <summary>
        /// A color converter of the one format to the other, to be initialized: on Windows a <see cref="ColorConverter"/>, of
        /// Media Foundation's video processor; on macOS and iOS 16 or later a <see cref="VideoToolboxColorConverter"/>; on
        /// Linux a <see cref="GStreamerColorConverter"/>. Where the system has none - Android, iOS before 16, Linux without
        /// GStreamer - or none of the options' color - Windows' of a matrix other than its own of the height - a
        /// <see cref="ManagedColorConverter"/>, of YUV to RGB and RGB to NV12. Each converts a frame as it goes in: read it out
        /// before the next.
        /// </summary>
        public static IMediaVideoTransform CreateColorConverter(Guid inputFormat, Guid outputFormat, ColorConverterOptions options)
        {
            ColorFormats.Validate(inputFormat, outputFormat, options);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && ColorConverter.Supports(inputFormat, outputFormat, options))
                return new ColorConverter(inputFormat, outputFormat, options);
            if (HasPixelTransfer && VideoToolboxColorConverter.Supports(inputFormat, outputFormat, options))
                return new VideoToolboxColorConverter(inputFormat, outputFormat, options);
            if (!IsAndroid && OperatingSystem.IsLinux() && GStreamerColorConverter.Supports(inputFormat, outputFormat))
                return new GStreamerColorConverter(inputFormat, outputFormat, options);
            if (ManagedColorConverter.Supports(inputFormat, outputFormat))
                return new ManagedColorConverter(inputFormat, outputFormat, options);

            throw new NotSupportedException($"No conversion of {inputFormat} to {outputFormat} on this system");
        }

        private const string Systems ="there are codecs on Windows 10 1809 or later, on macOS 11 and iOS 14 or later, on Android 8 or later, and on Linux of GStreamer";
    }
}
