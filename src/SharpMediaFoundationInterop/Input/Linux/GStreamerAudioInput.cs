using System;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Records PCM on Linux, of GStreamer: the device's source - PipeWire's or PulseAudio's, of the device monitor - or
    /// autoaudiosrc, the system's default, converted to the format asked for, in buffers of a second each, as waveIn's are.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed class GStreamerAudioInput : IAudioInput
    {
        private readonly string _deviceId;
        private GstAppPipeline _pipeline;
        private Thread _reader;
        private volatile bool _running;
        private bool _disposed;

        public uint SampleRate { get; }
        public uint Channels { get; }
        public uint BitsPerSample { get; }

        public event EventHandler<AudioInputEventArgs> FrameReceived;

        /// <param name="deviceId">The device's id, as <see cref="MediaDevices.GetAudioInputs"/> gives it; null for the default.</param>
        public GStreamerAudioInput(string deviceId, uint sampleRate, uint channels, uint bitsPerSample)
        {
            _deviceId = deviceId;
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

            string format = BitsPerSample switch { 8 => "U8", 16 => "S16LE", 24 => "S24LE", 32 => "S32LE", _ => throw new NotSupportedException($"No PCM of {BitsPerSample} bits") };
            string sink = $"audioconvert ! audioresample ! audio/x-raw,format={format},layout=interleaved,rate={SampleRate},channels={Channels} ! appsink name=sink sync=false";
            if (_deviceId == null)
            {
                _pipeline = new GstAppPipeline($"autoaudiosrc ! {sink}");
            }
            else
            {
                IntPtr source = GstDevices.CreateElement("Audio/Source", _deviceId)
                    ?? throw new InvalidOperationException($"There is no sound input {_deviceId}");
                _pipeline = new GstAppPipeline(source, sink, IntPtr.Zero);
            }
            _pipeline.Play();

            _running = true;
            _reader = new Thread(Read) { IsBackground = true, Name = "GStreamerAudioInput" };
            _reader.Start();
        }

        /// <summary>
        /// The recorded sound, gathered into buffers of a second, each raised as it is full: of one array, filled again for the
        /// next once raised, and one arguments - see <see cref="IAudioInput.FrameReceived"/>.
        /// </summary>
        private unsafe void Read()
        {
            int second = (int)(SampleRate * Channels * BitsPerSample / 8);
            var recorded = new AudioInputBuffer();
            byte[] data = recorded.Rent(second);
            int filled = 0;
            var wait = TimeSpan.FromMilliseconds(100);
            try
            {
                while (_running)
                {
                    IntPtr sample = _pipeline.Pull(wait);
                    if (sample == IntPtr.Zero)
                        continue;
                    try
                    {
                        if (second > 0 && GstFrames.MapBytes(sample, out IntPtr buffer, out var info))
                        {
                            try
                            {
                                // of the sample's buffer straight into the second's, raised each time it is full
                                var bytes = new ReadOnlySpan<byte>(info.Data, (int)info.Size);
                                while (!bytes.IsEmpty && _running)
                                {
                                    int count = Math.Min(bytes.Length, second - filled);
                                    bytes.Slice(0, count).CopyTo(data.AsSpan(filled));
                                    filled += count;
                                    bytes = bytes.Slice(count);
                                    if (filled == second)
                                    {
                                        filled = 0;
                                        FrameReceived?.Invoke(this, recorded.Args);
                                    }
                                }
                            }
                            finally
                            {
                                Gst.Unmap(buffer, ref info);
                            }
                        }
                    }
                    finally
                    {
                        Gst.gst_mini_object_unref(sample);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled)
                    Log.Error($"The sound input stopped: {ex.Message}");
            }
        }

        /// <summary>Stops recording; what is gathered and not yet a second is let go of.</summary>
        public void Reset()
        {
            if (!_running)
                return;
            _running = false;
            _reader?.Join();
            _pipeline?.Dispose();
            _pipeline = null;
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
