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

    /// <summary>What a camera can be told to do, as DirectShow's IAMCameraControl has it: of its values, in the camera's units.</summary>
    public enum CameraProperty
    {
        Pan,
        Tilt,
        Roll,
        Zoom,
        Exposure,
        Iris,
        Focus
    }

    /// <summary>A camera's pan, tilt, zoom, exposure and so on, of a camera that has them: on Windows.</summary>
    public interface ICameraControl
    {
        /// <summary>The values a property takes; false of a property the camera does not have.</summary>
        bool TryGetRange(CameraProperty property, out int min, out int max, out int step, out int defaultValue);

        bool TryGet(CameraProperty property, out int value, out bool auto);

        /// <summary>A value set, or of <paramref name="auto"/> the camera left to set it itself.</summary>
        bool TrySet(CameraProperty property, int value, bool auto = false);
    }
}
