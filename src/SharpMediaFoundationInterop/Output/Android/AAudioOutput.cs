using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AAudio;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// Plays PCM on Android, of an AAudio stream on the device's default output - Android lists its devices to Java alone.
    /// As waveOut does, it plays the buffers queued in turn, each raising <see cref="OnPlaybackCompleted"/> as it has been
    /// played, and its position stands still where they run out: the silence AAudio plays on is left out of it.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AAudioOutput : IAudioOutput
    {
        private readonly object _lock = new object();
        private readonly Queue<byte[]> _pending = new Queue<byte[]>();
        /// <summary>The end of each buffer queued and not yet played, in frames written.</summary>
        private readonly Queue<long> _queued = new Queue<long>();
        private readonly AutoResetEvent _available = new AutoResetEvent(false);

        private IntPtr _stream;
        private Thread _writer;
        private Thread _monitor;
        private volatile bool _stopping;
        private bool _paused;
        private long _enqueuedFrames;
        private long _writtenFrames;
        private long _readBase;
        private long _silentFrames;
        private uint _lastPosition;
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

        public AAudioOutput(uint sampleRate, uint channels, uint bitsPerSample)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        private int FrameBytes => (int)(Channels * BitsPerSample / 8);

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream != IntPtr.Zero)
                return;
            _stream = Open(DIRECTION_OUTPUT, SampleRate, Channels, BitsPerSample);
            int result = AAudioStream_requestStart(_stream);
            if (result != AAUDIO_OK)
                throw new InvalidOperationException($"The sound output did not start: {Text(result)}");
            _writer = new Thread(Write) { IsBackground = true, Name = "AAudioOutput" };
            _writer.Start();
            _monitor = new Thread(Monitor) { IsBackground = true, Name = "AAudioOutput.Monitor" };
            _monitor.Start();
        }

        /// <summary>The bytes played since the device was opened or last reset: of the frames AAudio has played of what was written.</summary>
        public uint GetPosition()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                // paused, it stands still: AAudio's count of the frames read catches up with what was in flight only a while after
                if (_paused)
                    return _lastPosition;
                long played = Math.Min(AAudioStream_getFramesRead(_stream) - _readBase - _silentFrames, _enqueuedFrames);
                _lastPosition = (uint)Math.Max(played * FrameBytes, _lastPosition);
                return _lastPosition;
            }
        }

        public void Enqueue(byte[] data, uint length)
        {
            ThrowIfNotInitialized();
            if (length == 0)
                return;
            lock (_lock)
            {
                // where all was played, AAudio played silence since: that is left out of the position
                if (_queued.Count == 0)
                {
                    long read = AAudioStream_getFramesRead(_stream) - _readBase - _silentFrames;
                    if (read > _writtenFrames)
                        _silentFrames += read - _writtenFrames;
                }
                _pending.Enqueue(data.AsSpan(0, (int)length).ToArray());
                _enqueuedFrames += length / FrameBytes;
                _queued.Enqueue(_enqueuedFrames);
            }
            _available.Set();
        }

        /// <summary>Writes the buffers queued to the stream in turn, as it has room: AAudio's write waits for it.</summary>
        private void Write()
        {
            while (!_stopping)
            {
                byte[] buffer;
                lock (_lock)
                    _pending.TryDequeue(out buffer);
                if (buffer == null)
                {
                    _available.WaitOne(50);
                    continue;
                }
                fixed (byte* p = buffer)
                {
                    int frames = buffer.Length / FrameBytes, done = 0;
                    while (done < frames && !_stopping)
                    {
                        int written = AAudioStream_write(_stream, p + done * FrameBytes, frames - done, 100_000_000);
                        if (written < 0)
                            break;
                        done += written;
                        lock (_lock)
                            _writtenFrames += written;
                    }
                }
            }
        }

        /// <summary>Raises <see cref="OnPlaybackCompleted"/> for each buffer AAudio has played the end of.</summary>
        private void Monitor()
        {
            while (!_stopping)
            {
                int completed = 0;
                lock (_lock)
                {
                    long played = AAudioStream_getFramesRead(_stream) - _readBase - _silentFrames;
                    while (_queued.Count > 0 && _queued.Peek() <= played)
                    {
                        _queued.Dequeue();
                        completed++;
                    }
                }
                for (int i = 0; i < completed; i++)
                    OnPlaybackCompleted?.Invoke(this, EventArgs.Empty);
                Thread.Sleep(10);
            }
        }

        /// <summary>Stops playing and lets go of all that is queued; the position starts again from 0.</summary>
        public void Reset()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                PauseAndWait(_stream);
                AAudioStream_requestFlush(_stream);
                _pending.Clear();
                _queued.Clear();
                _readBase = AAudioStream_getFramesRead(_stream);
                _enqueuedFrames = _writtenFrames = _silentFrames = 0;
                _lastPosition = 0;
                if (!_paused)
                    AAudioStream_requestStart(_stream);
            }
        }

        public void Pause()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                // the position stands still from here: what AAudio has in flight is played out before it is paused
                PauseAndWait(_stream);
                long played = Math.Min(AAudioStream_getFramesRead(_stream) - _readBase - _silentFrames, _enqueuedFrames);
                _lastPosition = (uint)Math.Max(played * FrameBytes, _lastPosition);
                _paused = true;
            }
        }

        public void Resume()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                _paused = false;
                AAudioStream_requestStart(_stream);
            }
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream == IntPtr.Zero)
                throw new InvalidOperationException("The sound output is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopping = true;
            _available.Set();
            _writer?.Join();
            _monitor?.Join();
            if (_stream != IntPtr.Zero)
            {
                AAudioStream_requestStop(_stream);
                AAudioStream_close(_stream);
                _stream = IntPtr.Zero;
            }
            _available.Dispose();
        }
    }
}
