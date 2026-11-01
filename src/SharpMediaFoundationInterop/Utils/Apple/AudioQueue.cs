using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Utils
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public void* AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public AudioStreamPacketDescription* PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
    }

    /// <summary>AudioQueue's functions, which play and record sound on macOS and iOS alike.</summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    internal static unsafe partial class AudioQueue
    {
        private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        public static readonly uint kAudioQueueProperty_CurrentDevice = FourCC("aqcd");

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueNewOutput(AudioStreamBasicDescription* format,
            delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, void> callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueNewInput(AudioStreamBasicDescription* format,
            delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, AudioTimeStamp*, uint, AudioStreamPacketDescription*, void> callback,
            IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out AudioQueueBuffer* buffer);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueEnqueueBuffer(IntPtr queue, AudioQueueBuffer* buffer, uint numPacketDescs, AudioStreamPacketDescription* packetDescs);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueStart(IntPtr queue, AudioTimeStamp* startTime);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueuePause(IntPtr queue);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueStop(IntPtr queue, byte immediate);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueReset(IntPtr queue);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueDispose(IntPtr queue, byte immediate);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueSetProperty(IntPtr queue, uint propertyID, void* data, uint dataSize);

        [LibraryImport(AudioToolboxLib)]
        public static partial int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, AudioTimeStamp* timeStamp, byte* discontinuity);

        /// <summary>The PCM format of the library's devices: interleaved, of signed integers, as WAVE's is.</summary>
        public static AudioStreamBasicDescription PcmFormat(uint sampleRate, uint channels, uint bitsPerSample)
        {
            uint frameBytes = channels * bitsPerSample / 8;
            return new AudioStreamBasicDescription
            {
                SampleRate = sampleRate,
                FormatID = kAudioFormatLinearPCM,
                // of 8 bits, WAVE's are unsigned
                FormatFlags = (bitsPerSample == 8 ? 0 : kAudioFormatFlagIsSignedInteger) | kAudioFormatFlagIsPacked,
                BytesPerPacket = frameBytes,
                FramesPerPacket = 1,
                BytesPerFrame = frameBytes,
                ChannelsPerFrame = channels,
                BitsPerChannel = bitsPerSample
            };
        }

        /// <summary>Tells the queue to use the device of the id given, rather than the system's default.</summary>
        public static int SetDevice(IntPtr queue, string uid)
        {
            IntPtr value = CreateString(uid);
            try
            {
                return AudioQueueSetProperty(queue, kAudioQueueProperty_CurrentDevice, &value, (uint)sizeof(IntPtr));
            }
            finally
            {
                CFRelease(value);
            }
        }
    }
}
