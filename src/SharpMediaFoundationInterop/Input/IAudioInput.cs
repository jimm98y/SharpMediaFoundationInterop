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

        /// <summary>
        /// Raised with each buffer recorded, on a thread of the device's. The arguments and their <see cref="AudioInputEventArgs.Data"/>
        /// are the input's own, filled again with the next buffer: valid while the event is raised, and no longer - what is
        /// kept of them is copied. Recording allocates nothing a buffer.
        /// </summary>
        event EventHandler<AudioInputEventArgs> FrameReceived;
    }

    /// <summary>
    /// A buffer of the sound an <see cref="IAudioInput"/> recorded: of the input's own, used again for the next buffer - see
    /// <see cref="IAudioInput.FrameReceived"/>.
    /// </summary>
    public class AudioInputEventArgs : EventArgs
    {
        /// <summary>PCM, of the device's format, the whole array: valid while the event is raised - copied, to be kept.</summary>
        public byte[] Data { get; private set; }

        public AudioInputEventArgs(byte[] data)
        {
            Data = data;
        }

        /// <summary>The arguments of the next buffer, in these: as an input raises its event again.</summary>
        internal AudioInputEventArgs Of(byte[] data)
        {
            Data = data;
            return this;
        }
    }

    /// <summary>
    /// An input's buffer of the exact size of what it recorded, and its arguments, both made once and used again for every
    /// buffer: made again only as the size changes. Of the thread the input raises its event on.
    /// </summary>
    internal sealed class AudioInputBuffer
    {
        private byte[] _data = Array.Empty<byte>();
        private AudioInputEventArgs _args;

        /// <summary>The array to fill with a buffer of so many bytes.</summary>
        public byte[] Rent(int length)
        {
            if (_data.Length != length)
                _data = new byte[length];
            return _data;
        }

        /// <summary>The arguments of the array <see cref="Rent"/> handed out, filled.</summary>
        public AudioInputEventArgs Args => _args == null ? _args = new AudioInputEventArgs(_data) : _args.Of(_data);
    }
}
