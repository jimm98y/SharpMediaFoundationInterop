using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// Plays PCM on Linux, of GStreamer: an appsrc of the format, and the device's sink - PipeWire's or PulseAudio's, of the
    /// device monitor - or autoaudiosink, the system's default. As waveOut does, it plays the buffers queued in turn, each
    /// raising <see cref="OnPlaybackCompleted"/> as it has been played, and its position stands still where they run out,
    /// counting no silence: the pipeline plays on, and what comes after is timed from then, the gap left out of the position.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerAudioOutput : IAudioOutput
    {
        private readonly string _deviceId;
        private readonly object _lock = new object();
        /// <summary>The end of each buffer queued and not yet played, in ticks of the stream.</summary>
        private readonly Queue<long> _queued = new Queue<long>();

        private GstAppPipeline _pipeline;
        private Thread _monitor;
        private volatile bool _stopping;
        private bool _running;
        private bool _paused;
        private long _enqueuedBytes;
        /// <summary>Where the next buffer starts, in ticks of the stream.</summary>
        private long _nextTime;
        /// <summary>The ticks of the stream that played nothing: of the gaps where the buffers ran out.</summary>
        private long _silentTime;
        private uint _lastPosition;

        /// <summary>
        /// How far ahead of the pipeline's clock a buffer after a gap is put: the sink's own latency, or it would be late and
        /// its start cut.
        /// </summary>
        private static readonly long GapLead = MediaUtils.TicksPerSecond / 10;
        private bool _disposed;

        public uint SampleRate { get; }
        public uint Channels { get; }
        public uint BitsPerSample { get; }

        public int QueuedFrames
        {
            get
            {
                lock (_lock)
                    return _queued.Count;
            }
        }

        public event EventHandler<EventArgs> OnPlaybackCompleted;

        /// <param name="deviceId">The device's id, as <see cref="MediaDevices.GetAudioOutputs"/> gives it; null for the default.</param>
        public GStreamerAudioOutput(string deviceId, uint sampleRate, uint channels, uint bitsPerSample)
        {
            _deviceId = deviceId;
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        private uint FrameBytes => Channels * BitsPerSample / 8;

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;

            string format = BitsPerSample switch { 8 => "U8", 16 => "S16LE", 24 => "S24LE", 32 => "S32LE", _ => throw new NotSupportedException($"No PCM of {BitsPerSample} bits") };
            string source = $"appsrc name=src format=time is-live=false block=false caps=\"audio/x-raw,format={format},layout=interleaved,rate={SampleRate},channels={Channels}\" ! audioconvert ! audioresample";
            if (_deviceId == null)
            {
                _pipeline = new GstAppPipeline($"{source} ! autoaudiosink");
            }
            else
            {
                IntPtr sink = GstDevices.CreateElement("Audio/Sink", _deviceId)
                    ?? throw new InvalidOperationException($"There is no sound output {_deviceId}");
                _pipeline = new GstAppPipeline(IntPtr.Zero, source, sink);
            }
            _pipeline.Pause();

            _monitor = new Thread(Monitor) { IsBackground = true, Name = "GStreamerAudioOutput" };
            _monitor.Start();
        }

        /// <summary>The bytes played since the device was opened or last reset: of the pipeline's position, which stands still while there is nothing to play.</summary>
        public uint GetPosition()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                long? ticks = _pipeline.Position;
                if (ticks.HasValue)
                {
                    long bytes = Math.Min(ToBytes(Math.Max(ticks.Value - _silentTime, 0)), _enqueuedBytes);
                    _lastPosition = (uint)Math.Max(bytes, _lastPosition);
                }
                return _lastPosition;
            }
        }

        public void Enqueue(byte[] data, uint length)
        {
            ThrowIfNotInitialized();
            if (length == 0)
                return;
            if (length > data.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            lock (_lock)
            {
                // the buffers' times run on from one to the next; but where they ran out, the next starts at the clock, ahead of
                // it by the sink's latency, and the silence between is left out of the position
                long start = _nextTime;
                if (_running && _queued.Count == 0 && _pipeline.Position is long now && now + GapLead > start)
                {
                    _silentTime += now + GapLead - start;
                    start = now + GapLead;
                }
                long duration = ToTicks(_enqueuedBytes + length) - ToTicks(_enqueuedBytes);
                _enqueuedBytes += length;
                _nextTime = start + duration;
                long end = _nextTime;
                _pipeline.Push(ReadOnlySpan<byte>.Empty, data.AsSpan(0, (int)length), start, duration);
                _queued.Enqueue(end);
                if (!_paused && !_running)
                {
                    _pipeline.Play();
                    _running = true;
                }
            }
        }

        /// <summary>Stops playing and lets go of all that is queued; the position starts again from 0.</summary>
        public void Reset()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                _pipeline.Flush();
                _pipeline.Pause();
                _running = false;
                _queued.Clear();
                _enqueuedBytes = 0;
                _nextTime = 0;
                _silentTime = 0;
                _lastPosition = 0;
            }
        }

        public void Pause()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                _paused = true;
                if (_running)
                {
                    _pipeline.Pause();
                    _running = false;
                }
            }
        }

        public void Resume()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                _paused = false;
                if (!_running && _queued.Count > 0)
                {
                    _pipeline.Play();
                    _running = true;
                }
            }
        }

        /// <summary>Raises <see cref="OnPlaybackCompleted"/> for each buffer the pipeline's position has passed the end of.</summary>
        private void Monitor()
        {
            while (!_stopping)
            {
                int completed = 0;
                lock (_lock)
                {
                    long? position = _pipeline?.Position;
                    if (position.HasValue)
                    {
                        while (_queued.Count > 0 && _queued.Peek() <= position.Value)
                        {
                            _queued.Dequeue();
                            completed++;
                        }
                    }
                }
                for (int i = 0; i < completed; i++)
                    OnPlaybackCompleted?.Invoke(this, EventArgs.Empty);
                Thread.Sleep(10);
            }
        }

        private long ToTicks(long bytes) => bytes / FrameBytes * MediaUtils.TicksPerSecond / SampleRate;

        private long ToBytes(long ticks) => ticks * SampleRate / MediaUtils.TicksPerSecond * FrameBytes;

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline == null)
                throw new InvalidOperationException("The sound output is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopping = true;
            _monitor?.Join();
            lock (_lock)
            {
                _pipeline?.Dispose();
                _pipeline = null;
            }
        }
    }
}
