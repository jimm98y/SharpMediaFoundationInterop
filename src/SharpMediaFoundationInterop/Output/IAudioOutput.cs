using System;
using SharpMediaFoundationInterop.Codecs;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// A device that plays PCM: of <see cref="MediaDevices.CreateAudioOutput"/>, made for a device and a format, which
    /// <see cref="IAudioDescriptor"/> tells. The sound is queued a buffer at a time and played in turn.
    /// </summary>
    public interface IAudioOutput : IDisposable, IAudioDescriptor
    {
        /// <summary>Opens the device, ready to play.</summary>
        void Initialize();

        /// <summary>The buffers queued and not yet played.</summary>
        int QueuedFrames { get; }

        /// <summary>The bytes played since the device was opened or last <see cref="Reset"/>.</summary>
        uint GetPosition();

        /// <summary>Queues <paramref name="length"/> bytes of <paramref name="data"/> to play, copying them.</summary>
        void Enqueue(byte[] data, uint length);

        /// <summary>Stops playing and lets go of all that is queued.</summary>
        void Reset();

        /// <summary>Stops playing where it is, keeping what is queued: <see cref="Resume"/> plays on from there.</summary>
        void Pause();

        void Resume();

        /// <summary>Raised as each queued buffer has been played, on a thread of the device's.</summary>
        event EventHandler<EventArgs> OnPlaybackCompleted;
    }
}
