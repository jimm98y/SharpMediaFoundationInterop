using System;
using SharpMediaFoundationInterop.Codecs;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// A device that records PCM: of <see cref="MediaDevices.CreateAudioInput"/>, made for a device and a format, which
    /// <see cref="IAudioDescriptor"/> tells.
    /// </summary>
    public interface IAudioInput : IDisposable, IAudioDescriptor
    {
        /// <summary>Opens the device and starts recording.</summary>
        void Initialize();

        /// <summary>Stops recording.</summary>
        void Reset();

        /// <summary>Raised with each buffer recorded, on a thread of the device's.</summary>
        event EventHandler<AudioInputEventArgs> FrameReceived;
    }

    /// <summary>A buffer of the sound an <see cref="IAudioInput"/> recorded.</summary>
    public class AudioInputEventArgs : EventArgs
    {
        /// <summary>PCM, of the device's format.</summary>
        public byte[] Data { get; private set; }

        public AudioInputEventArgs(byte[] data)
        {
            Data = data;
        }
    }
}
