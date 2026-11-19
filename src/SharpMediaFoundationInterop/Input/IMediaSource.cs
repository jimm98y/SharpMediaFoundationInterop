using System;
using SharpMediaFoundationInterop.Codecs;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>What captures media - a camera, a screen - and hands out its samples as they come.</summary>
    public interface IMediaSource : IDisposable, IMediaOutput
    {
        void Initialize();
        bool ReadSample(byte[] sampleBytes, out long timestamp);
    }

    public interface IMediaVideoSource : IMediaSource, IVideoDescriptor
    { }

    public interface IMediaAudioSource : IMediaSource, IAudioDescriptor
    { }

    /// <summary>
    /// What a camera can be told to do, as DirectShow's IAMCameraControl has it. Its values are of the platform's units, so a
    /// value is best had of <see cref="ICameraControl.TryGetRange"/> - a slider of its range works the same everywhere.
    /// </summary>
    public enum CameraProperty
    {
        /// <summary>Degrees on Windows, arc seconds on Linux; of no camera on Android, iOS and macOS.</summary>
        Pan,
        /// <summary>As <see cref="Pan"/>.</summary>
        Tilt,
        /// <summary>Degrees, on Windows alone.</summary>
        Roll,
        /// <summary>The camera's own units on Windows and Linux; the zoom's ratio times 100 on Android, iOS and macOS.</summary>
        Zoom,
        /// <summary>A power of 2 in seconds on Windows, 100 µs on Linux, microseconds on Android, iOS and macOS.</summary>
        Exposure,
        /// <summary>The camera's own units, on Windows and Linux.</summary>
        Iris,
        /// <summary>The camera's own units on Windows and Linux, diopters times 100 on Android, the lens' position of 0 to 1000 on iOS and macOS.</summary>
        Focus
    }

    /// <summary>
    /// A camera's pan, tilt, zoom, exposure and so on, of the properties it has: of the camera capture of Windows, Linux (a
    /// V4L2 camera's), Android, iOS and macOS. Of a camera shared with another app, none.
    /// </summary>
    public interface ICameraControl
    {
        /// <summary>The values a property takes; false of a property the camera does not have.</summary>
        bool TryGetRange(CameraProperty property, out int min, out int max, out int step, out int defaultValue);

        bool TryGet(CameraProperty property, out int value, out bool auto);

        /// <summary>A value set, or of <paramref name="auto"/> the camera left to set it itself.</summary>
        bool TrySet(CameraProperty property, int value, bool auto = false);
    }
}
