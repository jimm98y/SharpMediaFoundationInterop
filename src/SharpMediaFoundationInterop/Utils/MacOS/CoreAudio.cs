using System;
using System.Collections.Generic;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    /// <summary>A sound device of Core Audio's: its id, what AudioQueue is told to use it by, and its name.</summary>
    internal readonly record struct CoreAudioDevice(string Uid, string Name);

    /// <summary>Core Audio's devices, and AudioQueue's functions, which play and record through them.</summary>
    [SupportedOSPlatform("macos11.0")]
    internal static unsafe class CoreAudio
    {
        private const string CoreAudioLib = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
        private const string AudioToolboxLib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

        private const uint kAudioObjectSystemObject = 1;
        private static readonly uint kAudioHardwarePropertyDevices = FourCC("dev#");
        private static readonly uint kAudioDevicePropertyStreamConfiguration = FourCC("slay");
        private static readonly uint kAudioObjectPropertyName = FourCC("lnam");
        private static readonly uint kAudioDevicePropertyDeviceUID = FourCC("uid ");
        private static readonly uint kAudioObjectPropertyScopeGlobal = FourCC("glob");
        public static readonly uint kAudioObjectPropertyScopeInput = FourCC("inpt");
        public static readonly uint kAudioObjectPropertyScopeOutput = FourCC("outp");

        public static readonly uint kAudioQueueProperty_CurrentDevice = FourCC("aqcd");

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectGetPropertyDataSize(uint objectID, AudioObjectPropertyAddress* address, uint qualifierDataSize, void* qualifierData, out uint dataSize);

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectGetPropertyData(uint objectID, AudioObjectPropertyAddress* address, uint qualifierDataSize, void* qualifierData, uint* dataSize, void* data);

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

        /// <summary>The devices with streams of the scope given: input, or output.</summary>
        public static CoreAudioDevice[] Enumerate(uint scope)
        {
            var address = new AudioObjectPropertyAddress { Selector = kAudioHardwarePropertyDevices, Scope = kAudioObjectPropertyScopeGlobal };
            if (AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &address, 0, null, out uint size) != 0 || size == 0)
                return Array.Empty<CoreAudioDevice>();
            var ids = new uint[size / sizeof(uint)];
            fixed (uint* p = ids)
            {
                if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, null, &size, p) != 0)
                    return Array.Empty<CoreAudioDevice>();
            }

            var devices = new List<CoreAudioDevice>();
            foreach (uint id in ids)
            {
                if (ChannelCount(id, scope) == 0)
                    continue;
                string uid = GetStringProperty(id, kAudioDevicePropertyDeviceUID);
                if (uid != null)
                    devices.Add(new CoreAudioDevice(uid, GetStringProperty(id, kAudioObjectPropertyName) ?? uid));
            }
            return devices.ToArray();
        }

        /// <summary>The channels of the device's streams of the scope: of the AudioBufferList of its stream configuration.</summary>
        private static uint ChannelCount(uint id, uint scope)
        {
            var address = new AudioObjectPropertyAddress { Selector = kAudioDevicePropertyStreamConfiguration, Scope = scope };
            if (AudioObjectGetPropertyDataSize(id, &address, 0, null, out uint size) != 0 || size < sizeof(uint))
                return 0;
            byte* data = (byte*)NativeMemory.Alloc(size);
            try
            {
                if (AudioObjectGetPropertyData(id, &address, 0, null, &size, data) != 0)
                    return 0;
                uint buffers = *(uint*)data;
                uint channels = 0;
                // the buffers start past the count, aligned as their pointers are
                var buffer = (AudioBuffer*)(data + IntPtr.Size);
                for (uint i = 0; i < buffers; i++)
                    channels += buffer[i].NumberChannels;
                return channels;
            }
            finally
            {
                NativeMemory.Free(data);
            }
        }

        private static string GetStringProperty(uint id, uint selector)
        {
            var address = new AudioObjectPropertyAddress { Selector = selector, Scope = kAudioObjectPropertyScopeGlobal };
            IntPtr value = IntPtr.Zero;
            uint size = (uint)sizeof(IntPtr);
            if (AudioObjectGetPropertyData(id, &address, 0, null, &size, &value) != 0 || value == IntPtr.Zero)
                return null;
            try
            {
                return ObjC.GetCFString(value);
            }
            finally
            {
                CFRelease(value);
            }
        }
    }
}
