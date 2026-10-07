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
    internal static unsafe class AudioQueue
    {
        private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        public static readonly uint kAudioQueueProperty_CurrentDevice = FourCC("aqcd");

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueNewOutput(AudioStreamBasicDescription* format,
            delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, void> callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueNewInput(AudioStreamBasicDescription* format,
            delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, AudioTimeStamp*, uint, AudioStreamPacketDescription*, void> callback,
            IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueAllocateBuffer(IntPtr queue, uint byteSize, out AudioQueueBuffer* buffer);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueEnqueueBuffer(IntPtr queue, AudioQueueBuffer* buffer, uint numPacketDescs, AudioStreamPacketDescription* packetDescs);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueStart(IntPtr queue, AudioTimeStamp* startTime);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueuePause(IntPtr queue);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueStop(IntPtr queue, byte immediate);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueReset(IntPtr queue);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueDispose(IntPtr queue, byte immediate);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueSetProperty(IntPtr queue, uint propertyID, void* data, uint dataSize);

        [DllImport(AudioToolboxLib)]
        public static extern int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, AudioTimeStamp* timeStamp, byte* discontinuity);

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
