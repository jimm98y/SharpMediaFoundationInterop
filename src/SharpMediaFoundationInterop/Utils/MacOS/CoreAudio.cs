using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Utils
{
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

        private const uint kAudioObjectSystemObject = 1;
        private static readonly uint kAudioHardwarePropertyDevices = FourCC("dev#");
        private static readonly uint kAudioDevicePropertyStreamConfiguration = FourCC("slay");
        private static readonly uint kAudioObjectPropertyName = FourCC("lnam");
        private static readonly uint kAudioDevicePropertyDeviceUID = FourCC("uid ");
        private static readonly uint kAudioObjectPropertyScopeGlobal = FourCC("glob");
        public static readonly uint kAudioObjectPropertyScopeInput = FourCC("inpt");
        public static readonly uint kAudioObjectPropertyScopeOutput = FourCC("outp");


        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectGetPropertyDataSize(uint objectID, AudioObjectPropertyAddress* address, uint qualifierDataSize, void* qualifierData, out uint dataSize);

        [DllImport(CoreAudioLib)]
        private static extern int AudioObjectGetPropertyData(uint objectID, AudioObjectPropertyAddress* address, uint qualifierDataSize, void* qualifierData, uint* dataSize, void* data);

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
