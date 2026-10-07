using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>A time of Core Media's: a value over a timescale.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;

        private const uint Valid = 1;

        /// <summary>The library's times are of 100 ns ticks, as Media Foundation's are.</summary>
        public const int TicksTimescale = 10_000_000;

        public static CMTime FromTicks(long ticks) => new CMTime { Value = ticks, Timescale = TicksTimescale, Flags = Valid };

        public static CMTime Invalid => default;

        public bool IsValid => (Flags & Valid) != 0 && Timescale != 0;

        public long ToTicks() => !IsValid ? 0 : Timescale == TicksTimescale ? Value : (long)((Int128)Value * TicksTimescale / Timescale);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CMVideoDimensions
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CMSampleTimingInfo
    {
        public CMTime Duration;
        public CMTime PresentationTimeStamp;
        public CMTime DecodeTimeStamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct VTDecompressionOutputCallbackRecord
    {
        public delegate* unmanaged<IntPtr, IntPtr, int, uint, IntPtr, CMTime, CMTime, void> Callback;
        public IntPtr RefCon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStreamPacketDescription
    {
        public long StartOffset;
        public uint VariableFramesInPacket;
        public uint DataByteSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AudioBuffer
    {
        public uint NumberChannels;
        public uint DataByteSize;
        public void* Data;
    }

    /// <summary>An AudioBufferList of the one buffer: interleaved PCM.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioBufferList
    {
        public uint NumberBuffers;
        public AudioBuffer Buffer;
    }

    /// <summary>An AudioChannelLayout given by its tag alone, of the size of the struct with one channel description.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    internal struct AudioChannelLayout
    {
        public uint Tag;
        public uint Bitmap;
        public uint NumberChannelDescriptions;
    }

    /// <summary>
    /// What of Core Foundation, Core Media, Core Video, VideoToolbox and AudioToolbox the codecs of macOS are made of: the
    /// system's frameworks, called straight, as Media Foundation is on Windows.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    internal static unsafe class AppleNative
    {
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreMediaLib = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
        private const string CoreVideoLib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
        private const string VideoToolboxLib = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
        private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        public static uint FourCC(string code) =>
            (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);

        public static string FourCCString(int status)
        {
            // an OSStatus is a four character code as often as it is a number
            uint value = (uint)status;
            Span<char> chars = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                char c = (char)((value >> (24 - 8 * i)) & 0xFF);
                if (c < 0x20 || c > 0x7E)
                    return status.ToString();
                chars[i] = c;
            }
            return $"'{new string(chars)}' ({status})";
        }

        #region Core Foundation

        private const uint kCFStringEncodingUTF8 = 0x08000100;
        private const int kCFNumberSInt32Type = 3;
        private const int kCFNumberFloat64Type = 6;

        [DllImport(CoreFoundationLib)]
        public static extern void CFRelease(IntPtr cf);

        [DllImport(CoreFoundationLib)]
        public static extern IntPtr CFRetain(IntPtr cf);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte* cStr, uint encoding);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFNumberCreate(IntPtr allocator, int type, void* value);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [DllImport(CoreFoundationLib)]
        private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

        private static readonly IntPtr CoreFoundation = NativeLibrary.Load(CoreFoundationLib);
        private static readonly IntPtr CoreMedia = NativeLibrary.Load(CoreMediaLib);
        private static readonly IntPtr CoreVideo = NativeLibrary.Load(CoreVideoLib);

        private static readonly IntPtr kCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr kCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

        /// <summary>The value of a constant a framework exports: a CFStringRef, of a key.</summary>
        private static IntPtr Constant(IntPtr library, string name) => *(IntPtr*)NativeLibrary.GetExport(library, name);

        public static readonly IntPtr kCVPixelBufferPixelFormatTypeKey = Constant(CoreVideo, "kCVPixelBufferPixelFormatTypeKey");
        public static readonly IntPtr kCVPixelBufferWidthKey = Constant(CoreVideo, "kCVPixelBufferWidthKey");
        public static readonly IntPtr kCVPixelBufferHeightKey = Constant(CoreVideo, "kCVPixelBufferHeightKey");
        public static readonly IntPtr kCMSampleAttachmentKey_NotSync = Constant(CoreMedia, "kCMSampleAttachmentKey_NotSync");
        public static readonly IntPtr kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms =
            Constant(CoreMedia, "kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms");

        public static IntPtr CreateString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            fixed (byte* p = bytes)
                return CFStringCreateWithCString(IntPtr.Zero, p, kCFStringEncodingUTF8);
        }

        public static IntPtr CreateData(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
                return CFDataCreate(IntPtr.Zero, p, data.Length);
        }

        public static IntPtr CreateNumber(int value) => CFNumberCreate(IntPtr.Zero, kCFNumberSInt32Type, &value);

        public static IntPtr CreateNumber(double value) => CFNumberCreate(IntPtr.Zero, kCFNumberFloat64Type, &value);

        /// <summary>kCFBooleanTrue or kCFBooleanFalse: constants, not to be released.</summary>
        public static IntPtr Boolean(bool value) => Constant(CoreFoundation, value ? "kCFBooleanTrue" : "kCFBooleanFalse");

        public static IntPtr CreateDictionary() =>
            CFDictionaryCreateMutable(IntPtr.Zero, 0, kCFTypeDictionaryKeyCallBacks, kCFTypeDictionaryValueCallBacks);

        /// <summary>Sets a value of the dictionary, which retains it: the caller lets go of its own.</summary>
        public static void SetValue(IntPtr dictionary, IntPtr key, IntPtr value) => CFDictionarySetValue(dictionary, key, value);

        #endregion

        #region Core Media

        public const uint kCMBlockBufferAssureMemoryNowFlag = 1;

        [DllImport(CoreMediaLib)]
        public static extern int CMVideoFormatDescriptionCreate(IntPtr allocator, uint codecType, int width, int height, IntPtr extensions, out IntPtr formatDescription);

        [DllImport(CoreMediaLib)]
        public static extern int CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr allocator, nuint parameterSetCount,
            byte** parameterSetPointers, nuint* parameterSetSizes, int nalUnitHeaderLength, out IntPtr formatDescription);

        [DllImport(CoreMediaLib)]
        public static extern int CMVideoFormatDescriptionCreateFromHEVCParameterSets(IntPtr allocator, nuint parameterSetCount,
            byte** parameterSetPointers, nuint* parameterSetSizes, int nalUnitHeaderLength, IntPtr extensions, out IntPtr formatDescription);

        [DllImport(CoreMediaLib)]
        public static extern int CMBlockBufferCreateWithMemoryBlock(IntPtr structureAllocator, void* memoryBlock, nuint blockLength,
            IntPtr blockAllocator, IntPtr customBlockSource, nuint offsetToData, nuint dataLength, uint flags, out IntPtr blockBuffer);

        [DllImport(CoreMediaLib)]
        public static extern int CMBlockBufferReplaceDataBytes(void* sourceBytes, IntPtr destinationBuffer, nuint offsetIntoDestination, nuint dataLength);

        [DllImport(CoreMediaLib)]
        public static extern int CMSampleBufferCreateReady(IntPtr allocator, IntPtr dataBuffer, IntPtr formatDescription, nint numSamples,
            nint numSampleTimingEntries, CMSampleTimingInfo* sampleTimingArray, nint numSampleSizeEntries, nuint* sampleSizeArray, out IntPtr sampleBuffer);

        [DllImport(CoreMediaLib)]
        public static extern IntPtr CMSampleBufferGetImageBuffer(IntPtr sampleBuffer);

        [DllImport(CoreMediaLib)]
        public static extern IntPtr CMSampleBufferGetDataBuffer(IntPtr sampleBuffer);

        [DllImport(CoreMediaLib)]
        public static extern IntPtr CMSampleBufferGetFormatDescription(IntPtr sampleBuffer);

        [DllImport(CoreMediaLib)]
        public static extern nuint CMBlockBufferGetDataLength(IntPtr blockBuffer);

        [DllImport(CoreMediaLib)]
        public static extern int CMBlockBufferCopyDataBytes(IntPtr blockBuffer, nuint offsetToData, nuint dataLength, void* destination);

        [DllImport(CoreMediaLib)]
        public static extern int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(IntPtr videoDesc, nuint parameterSetIndex,
            out byte* parameterSetPointer, out nuint parameterSetSize, out nuint parameterSetCount, out int nalUnitHeaderLength);

        [DllImport(CoreMediaLib)]
        public static extern int CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(IntPtr videoDesc, nuint parameterSetIndex,
            out byte* parameterSetPointer, out nuint parameterSetSize, out nuint parameterSetCount, out int nalUnitHeaderLength);

        [DllImport(CoreMediaLib)]
        public static extern CMTime CMSampleBufferGetPresentationTimeStamp(IntPtr sampleBuffer);

        [DllImport(CoreMediaLib)]
        public static extern IntPtr CMSampleBufferGetSampleAttachmentsArray(IntPtr sampleBuffer, byte createIfNecessary);

        [DllImport(CoreMediaLib)]
        public static extern CMVideoDimensions CMVideoFormatDescriptionGetDimensions(IntPtr videoDesc);

        [DllImport(CoreFoundationLib)]
        public static extern nint CFArrayGetCount(IntPtr array);

        [DllImport(CoreFoundationLib)]
        public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

        [DllImport(CoreFoundationLib)]
        public static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        #endregion

        #region Core Video

        public const ulong kCVPixelBufferLock_ReadOnly = 1;
        public static readonly uint kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange = FourCC("420v");
        public static readonly uint kCVPixelFormatType_420YpCbCr8BiPlanarFullRange = FourCC("420f");
        public static readonly uint kCVPixelFormatType_32BGRA = FourCC("BGRA");

        [DllImport(CoreVideoLib)]
        public static extern int CVPixelBufferPoolCreatePixelBuffer(IntPtr allocator, IntPtr pixelBufferPool, out IntPtr pixelBuffer);

        [DllImport(CoreVideoLib)]
        public static extern int CVPixelBufferLockBaseAddress(IntPtr pixelBuffer, ulong lockFlags);

        [DllImport(CoreVideoLib)]
        public static extern int CVPixelBufferUnlockBaseAddress(IntPtr pixelBuffer, ulong unlockFlags);

        [DllImport(CoreVideoLib)]
        public static extern uint CVPixelBufferGetPixelFormatType(IntPtr pixelBuffer);

        [DllImport(CoreVideoLib)]
        public static extern nuint CVPixelBufferGetWidth(IntPtr pixelBuffer);

        [DllImport(CoreVideoLib)]
        public static extern nuint CVPixelBufferGetHeight(IntPtr pixelBuffer);

        [DllImport(CoreVideoLib)]
        public static extern IntPtr CVPixelBufferGetBaseAddress(IntPtr pixelBuffer);

        [DllImport(CoreVideoLib)]
        public static extern nuint CVPixelBufferGetBytesPerRow(IntPtr pixelBuffer);

        [DllImport(CoreVideoLib)]
        public static extern IntPtr CVPixelBufferGetBaseAddressOfPlane(IntPtr pixelBuffer, nuint planeIndex);

        [DllImport(CoreVideoLib)]
        public static extern nuint CVPixelBufferGetBytesPerRowOfPlane(IntPtr pixelBuffer, nuint planeIndex);

        [DllImport(CoreVideoLib)]
        public static extern nuint CVPixelBufferGetWidthOfPlane(IntPtr pixelBuffer, nuint planeIndex);

        [DllImport(CoreVideoLib)]
        public static extern nuint CVPixelBufferGetHeightOfPlane(IntPtr pixelBuffer, nuint planeIndex);

        #endregion

        #region VideoToolbox

        public const uint kVTDecodeFrame_EnableTemporalProcessing = 1 << 3;
        public const uint kVTDecodeInfo_FrameDropped = 1 << 1;

        public const int kVTCouldNotFindVideoDecoderErr = -12906;
        public const int kVTVideoDecoderBadDataErr = -12909;
        public const int kVTVideoDecoderUnsupportedDataFormatErr = -12910;
        public const int kVTVideoDecoderNotAvailableNowErr = -12913;
        /// <summary>The configuration of the stream is not one the decoder takes: of MPEG-4 Part 2, one of the advanced simple profile.</summary>
        public const int codecBadDataErr = -8969;

        [DllImport(VideoToolboxLib)]
        public static extern int VTDecompressionSessionCreate(IntPtr allocator, IntPtr videoFormatDescription, IntPtr videoDecoderSpecification,
            IntPtr destinationImageBufferAttributes, VTDecompressionOutputCallbackRecord* outputCallback, out IntPtr decompressionSession);

        [DllImport(VideoToolboxLib)]
        public static extern int VTDecompressionSessionDecodeFrame(IntPtr session, IntPtr sampleBuffer, uint decodeFlags, IntPtr sourceFrameRefCon, out uint infoFlagsOut);

        [DllImport(VideoToolboxLib)]
        public static extern int VTDecompressionSessionFinishDelayedFrames(IntPtr session);

        [DllImport(VideoToolboxLib)]
        public static extern int VTDecompressionSessionWaitForAsynchronousFrames(IntPtr session);

        [DllImport(VideoToolboxLib)]
        public static extern void VTDecompressionSessionInvalidate(IntPtr session);

        [DllImport(VideoToolboxLib)]
        public static extern byte VTDecompressionSessionCanAcceptFormatDescription(IntPtr session, IntPtr newFormatDescription);

        [DllImport(VideoToolboxLib)]
        public static extern byte VTIsHardwareDecodeSupported(uint codecType);

        private static readonly IntPtr VideoToolbox = NativeLibrary.Load(VideoToolboxLib);

        /// <summary>A key or value of VideoToolbox's: a CFStringRef it exports, of the name its header gives it.</summary>
        public static IntPtr VideoToolboxConstant(string name) => Constant(VideoToolbox, name);

        [DllImport(VideoToolboxLib)]
        public static extern int VTCompressionSessionCreate(IntPtr allocator, int width, int height, uint codecType, IntPtr encoderSpecification,
            IntPtr sourceImageBufferAttributes, IntPtr compressedDataAllocator,
            delegate* unmanaged<IntPtr, IntPtr, int, uint, IntPtr, void> outputCallback, IntPtr outputCallbackRefCon, out IntPtr compressionSession);

        [DllImport(VideoToolboxLib)]
        public static extern int VTSessionSetProperty(IntPtr session, IntPtr propertyKey, IntPtr propertyValue);

        [DllImport(VideoToolboxLib)]
        public static extern int VTCompressionSessionPrepareToEncodeFrames(IntPtr session);

        [DllImport(VideoToolboxLib)]
        public static extern IntPtr VTCompressionSessionGetPixelBufferPool(IntPtr session);

        [DllImport(VideoToolboxLib)]
        public static extern int VTCompressionSessionEncodeFrame(IntPtr session, IntPtr imageBuffer, CMTime presentationTimeStamp, CMTime duration,
            IntPtr frameProperties, IntPtr sourceFrameRefcon, out uint infoFlagsOut);

        [DllImport(VideoToolboxLib)]
        public static extern int VTCompressionSessionCompleteFrames(IntPtr session, CMTime completeUntilPresentationTimeStamp);

        [DllImport(VideoToolboxLib)]
        public static extern void VTCompressionSessionInvalidate(IntPtr session);

        [DllImport(VideoToolboxLib)]
        public static extern void VTRegisterSupplementalVideoDecoderIfAvailable(uint codecType);

        #endregion

        #region Core Graphics

        private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(CoreGraphicsLib)]
        public static extern int CGGetActiveDisplayList(uint maxDisplays, uint* activeDisplays, out uint displayCount);

        [DllImport(CoreGraphicsLib)]
        public static extern uint CGMainDisplayID();

        [DllImport(CoreGraphicsLib)]
        public static extern int CGDisplayIsBuiltin(uint display);

        [DllImport(CoreGraphicsLib)]
        public static extern IntPtr CGDisplayCopyDisplayMode(uint display);

        [DllImport(CoreGraphicsLib)]
        public static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

        [DllImport(CoreGraphicsLib)]
        public static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);

        [DllImport(CoreGraphicsLib)]
        public static extern void CGDisplayModeRelease(IntPtr mode);

        [DllImport(CoreGraphicsLib)]
        public static extern byte CGPreflightScreenCaptureAccess();

        [DllImport(CoreGraphicsLib)]
        public static extern byte CGRequestScreenCaptureAccess();

        #endregion

        #region AudioToolbox

        public static readonly uint kAudioFormatLinearPCM = FourCC("lpcm");
        public const uint kAudioFormatFlagIsFloat = 1;
        public const uint kAudioFormatFlagIsSignedInteger = 4;
        public const uint kAudioFormatFlagIsPacked = 8;

        public static readonly uint kAudioFormatProperty_Decoders = FourCC("avde");
        public static readonly uint kAudioFormatProperty_Encoders = FourCC("aven");
        public static readonly uint kAudioConverterCompressionMagicCookie = FourCC("cmgc");
        public static readonly uint kAudioConverterPropertyMaximumOutputPacketSize = FourCC("xops");
        public static readonly uint kAudioFormatProperty_FormatInfo = FourCC("fmti");
        public static readonly uint kAudioConverterDecompressionMagicCookie = FourCC("dmgc");
        public static readonly uint kAudioConverterOutputChannelLayout = FourCC("ocl ");

        [DllImport(AudioToolboxLib)]
        public static extern int AudioFormatGetPropertyInfo(uint propertyID, uint specifierSize, void* specifier, out uint propertyDataSize);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioFormatGetProperty(uint propertyID, uint specifierSize, void* specifier, uint* propertyDataSize, void* propertyData);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterNew(AudioStreamBasicDescription* sourceFormat, AudioStreamBasicDescription* destinationFormat, out IntPtr audioConverter);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterDispose(IntPtr audioConverter);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterReset(IntPtr audioConverter);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterGetPropertyInfo(IntPtr audioConverter, uint propertyID, out uint size, out byte writable);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterGetProperty(IntPtr audioConverter, uint propertyID, uint* ioPropertyDataSize, void* outPropertyData);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterSetProperty(IntPtr audioConverter, uint propertyID, uint propertyDataSize, void* propertyData);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioConverterFillComplexBuffer(IntPtr audioConverter,
            delegate* unmanaged<IntPtr, uint*, AudioBufferList*, AudioStreamPacketDescription**, IntPtr, int> inputDataProc,
            IntPtr inputDataProcUserData, uint* ioOutputDataPacketSize, AudioBufferList* outOutputData, AudioStreamPacketDescription* outPacketDescription);

        #endregion
    }
}
