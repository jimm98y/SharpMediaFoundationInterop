using System;
using System.IO;
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
    public sealed class GStreamerAudioInput : IAudioInput
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

        /// <summary>The recorded sound, gathered into buffers of a second, each raised as it is full.</summary>
        private void Read()
        {
            int second = (int)(SampleRate * Channels * BitsPerSample / 8);
            var gathered = new MemoryStream(second * 2);
            try
            {
                while (_running)
                {
                    IntPtr sample = _pipeline.Pull(TimeSpan.FromMilliseconds(100));
                    if (sample == IntPtr.Zero)
                        continue;
                    try
                    {
                        gathered.Write(GstFrames.Bytes(sample));
                    }
                    finally
                    {
                        Gst.gst_mini_object_unref(sample);
                    }

                    while (gathered.Length >= second && _running)
                    {
                        var data = new byte[second];
                        var all = gathered.GetBuffer();
                        Buffer.BlockCopy(all, 0, data, 0, second);
                        int rest = (int)gathered.Length - second;
                        Buffer.BlockCopy(all, second, all, 0, rest);
                        gathered.SetLength(rest);
                        FrameReceived?.Invoke(this, new AudioInputEventArgs(data));
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
