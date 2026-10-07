using System;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AAudio;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Records PCM on Android, of an AAudio stream from the device's default input, in buffers of a second each, as waveIn's
    /// are. The app must hold the RECORD_AUDIO permission, granted by the user - which only its Java side can ask for.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AAudioInput : IAudioInput
    {
        private IntPtr _stream;
        private Thread _reader;
        private volatile bool _running;
        private bool _disposed;

        public uint SampleRate { get; }
        public uint Channels { get; }
        public uint BitsPerSample { get; }

        public event EventHandler<AudioInputEventArgs> FrameReceived;

        public AAudioInput(uint sampleRate, uint channels, uint bitsPerSample)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        /// <summary>Opens the device and starts recording.</summary>
        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
                return;
            _stream = Open(DIRECTION_INPUT, SampleRate, Channels, BitsPerSample);
            int result = AAudioStream_requestStart(_stream);
            if (result != AAUDIO_OK)
                throw new InvalidOperationException($"The sound input did not start: {Text(result)}");
            _running = true;
            _reader = new Thread(Read) { IsBackground = true, Name = "AAudioInput" };
            _reader.Start();
        }

        /// <summary>Reads the stream, a second at a time, raising each second as it is full.</summary>
        private void Read()
        {
            int frameBytes = (int)(Channels * BitsPerSample / 8);
            int frames = (int)SampleRate;
            var second = new byte[frames * frameBytes];
            int filled = 0;
            while (_running)
            {
                int read;
                fixed (byte* p = second)
                    read = AAudioStream_read(_stream, p + filled * frameBytes, frames - filled, 100_000_000);
                if (read < 0)
                {
                    if (Log.ErrorEnabled)
                        Log.Error($"The sound input stopped: {Text(read)}");
                    break;
                }
                filled += read;
                if (filled == frames && _running)
                {
                    FrameReceived?.Invoke(this, new AudioInputEventArgs(second));
                    second = new byte[frames * frameBytes];
                    filled = 0;
                }
            }
        }

        /// <summary>Stops recording; what is gathered and not yet a second is let go of.</summary>
        public void Reset()
        {
            if (!_running)
                return;
            _running = false;
            _reader?.Join();
            AAudioStream_requestStop(_stream);
            AAudioStream_close(_stream);
            _stream = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Reset();
        }
    }
}
