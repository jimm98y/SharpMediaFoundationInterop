using System;
using System.Globalization;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Input;
using SharpMediaFoundationInterop.Output;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop
{
    /// <summary>A device of <see cref="MediaDevices"/>: a speaker, a microphone, a camera or a screen.</summary>
    public sealed class MediaDevice
    {
        /// <summary>What the system knows the device by, which <see cref="MediaDevices"/> opens it by; not for showing.</summary>
        public string Id { get; }

        /// <summary>The device's name, for showing.</summary>
        public string Name { get; }

        public MediaDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// The sound and picture devices of the system it runs on: what plays and records sound, behind <see cref="IAudioOutput"/>
    /// and <see cref="IAudioInput"/>, and what captures a camera or the screen, behind <see cref="IMediaVideoSource"/>. On
    /// Windows 10 1809 or later, waveOut's, waveIn's, Media Foundation's and DXGI's; on macOS 11 or later AudioQueue's,
    /// AVFoundation's and - of macOS 12.3 - ScreenCaptureKit's. Elsewhere there are none yet: the lists are empty, and
    /// creating one throws <see cref="PlatformNotSupportedException"/>. Each is to be initialized; a device left null is the
    /// system's default. On macOS the user is asked, the first time, whether the app may use the microphone, the camera or
    /// the screen.
    /// </summary>
    public static class MediaDevices
    {
        [SupportedOSPlatformGuard("windows10.0.17763.0")]
        private static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

        [SupportedOSPlatformGuard("macos11.0")]
        private static bool IsMacOS => OperatingSystem.IsMacOSVersionAtLeast(11);

        [SupportedOSPlatformGuard("macos12.3")]
        private static bool IsMacOSScreenCapture => OperatingSystem.IsMacOSVersionAtLeast(12, 3);

        /// <summary>What can play sound.</summary>
        public static MediaDevice[] GetAudioOutputs()
        {
            if (IsMacOS)
                return ToDevices(CoreAudio.Enumerate(CoreAudio.kAudioObjectPropertyScopeOutput));
            if (!IsSupported)
                return Array.Empty<MediaDevice>();
            var devices = WaveOut.Enumerate();
            var result = new MediaDevice[devices.Length];
            for (int i = 0; i < devices.Length; i++)
                result[i] = new MediaDevice(devices[i].DeviceID.ToString(), devices[i].Name);
            return result;
        }

        /// <summary>What can record sound.</summary>
        public static MediaDevice[] GetAudioInputs()
        {
            if (IsMacOS)
                return ToDevices(CoreAudio.Enumerate(CoreAudio.kAudioObjectPropertyScopeInput));
            if (!IsSupported)
                return Array.Empty<MediaDevice>();
            var devices = WaveIn.Enumerate();
            var result = new MediaDevice[devices.Length];
            for (int i = 0; i < devices.Length; i++)
                result[i] = new MediaDevice(devices[i].DeviceID.ToString(), devices[i].Name);
            return result;
        }

        /// <summary>The cameras.</summary>
        public static MediaDevice[] GetCameras()
        {
            if (IsMacOS)
            {
                var cameras = AVFoundationCapture.Enumerate();
                return Array.ConvertAll(cameras, c => new MediaDevice(c.UniqueID, c.Name));
            }
            if (!IsSupported)
                return Array.Empty<MediaDevice>();
            var devices = DeviceCapture.Enumerate();
            var result = new MediaDevice[devices.Length];
            for (int i = 0; i < devices.Length; i++)
                result[i] = new MediaDevice(devices[i].ID, devices[i].Name);
            return result;
        }

        /// <summary>The screens, the primary first.</summary>
        public static MediaDevice[] GetScreens()
        {
            if (IsMacOSScreenCapture)
            {
                var screens = ScreenCaptureKitCapture.Enumerate();
                return Array.ConvertAll(screens, s => new MediaDevice(s.DisplayID.ToString(CultureInfo.InvariantCulture), s.Name));
            }
            if (!IsSupported)
                return Array.Empty<MediaDevice>();
            var devices = ScreenCapture.Enumerate();
            var result = new MediaDevice[devices.Length];
            for (int i = 0; i < devices.Length; i++)
                result[i] = new MediaDevice($"{devices[i].AdapterID}:{devices[i].OutputID}", devices[i].DeviceName);
            return result;
        }

        /// <summary>Plays PCM of the format given, on the device given: the system's default where it is null.</summary>
        public static IAudioOutput CreateAudioOutput(uint sampleRate, uint channels, uint bitsPerSample, MediaDevice device = null)
        {
            if (IsSupported)
                return new WaveOut(device == null ? WaveOut.WAVE_MAPPER : uint.Parse(device.Id), sampleRate, channels, bitsPerSample);
            if (IsMacOS)
                return new AudioQueueOutput(device?.Id, sampleRate, channels, bitsPerSample);

            throw new PlatformNotSupportedException(NoDevice("sound output"));
        }

        /// <summary>Records PCM of the format given, from the device given: the system's default where it is null.</summary>
        public static IAudioInput CreateAudioInput(uint sampleRate, uint channels, uint bitsPerSample, MediaDevice device = null)
        {
            if (IsSupported)
                return new WaveIn(device == null ? WaveIn.WAVE_MAPPER : uint.Parse(device.Id), sampleRate, channels, bitsPerSample);
            if (IsMacOS)
                return new AudioQueueInput(device?.Id, sampleRate, channels, bitsPerSample);

            throw new PlatformNotSupportedException(NoDevice("sound input"));
        }

        /// <summary>
        /// Captures the camera given - the first where it is null - in the format it gives best, which
        /// <see cref="IMediaOutput.OutputFormat"/> tells once it is initialized; each frame with the time it was taken.
        /// </summary>
        public static IMediaVideoSource CreateCameraCapture(MediaDevice device = null)
        {
            if (IsSupported)
                return new DeviceCapture(device?.Id);
            if (IsMacOS)
                return new AVFoundationCapture(device?.Id);

            throw new PlatformNotSupportedException(NoDevice("camera capture"));
        }

        /// <summary>
        /// Captures the screen given - the primary where it is null - as 32 bit BGRA: bottom-up, the first row the bottom
        /// one, as a video transform takes RGB; or top-down, as a bitmap is. A frame is handed out only where the screen
        /// changed.
        /// </summary>
        public static IMediaVideoSource CreateScreenCapture(MediaDevice device = null, bool topDown = false)
        {
            if (IsSupported)
            {
                uint adapter = 0, output = 0;
                if (device != null)
                {
                    var ids = device.Id.Split(':');
                    adapter = uint.Parse(ids[0]);
                    output = uint.Parse(ids[1]);
                }
                return new ScreenCapture(adapter, output) { BottomUp = !topDown };
            }
            if (IsMacOSScreenCapture)
                return new ScreenCaptureKitCapture(device == null ? 0 : uint.Parse(device.Id, CultureInfo.InvariantCulture)) { BottomUp = !topDown };

            throw new PlatformNotSupportedException(NoDevice("screen capture"));
        }

        [SupportedOSPlatform("macos11.0")]
        private static MediaDevice[] ToDevices(CoreAudioDevice[] devices) => Array.ConvertAll(devices, d => new MediaDevice(d.Uid, d.Name));

        private static string NoDevice(string what) =>
            $"No {what} on this system: there are devices on Windows 10 1809 or later, and on macOS 11 or later - of screen capture, 12.3";
    }
}
