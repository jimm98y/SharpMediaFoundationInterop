using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AudioQueue;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// Plays PCM on macOS and iOS, of an AudioQueue: on macOS on the device of the id given - a Core Audio device's UID - or
    /// the system's default; on iOS on the route the system chooses, the app's audio session moved to Playback first. As waveOut does, it plays the buffers queued in turn, and stops where they run out - its position stands still
    /// until there is more - rather than playing silence on.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    public sealed unsafe class AudioQueueOutput : IAudioOutput
    {
        private readonly string _deviceUid;
        private readonly object _lock = new object();
        /// <summary>The queue's buffers not playing: AudioQueue's are freed only with the queue, so they are kept for the next.</summary>
        private readonly ConcurrentBag<IntPtr> _freeBuffers = new ConcurrentBag<IntPtr>();

        private IntPtr _queue;
        private GCHandle _self;
        private int _queuedFrames;
        private volatile bool _running;
        private bool _paused;
        private bool _disposed;
        private uint _lastPosition;
        /// <summary>The bytes queued since the device was opened or last reset: what the position can come to, at most.</summary>
        private long _enqueuedBytes;
        /// <summary>
        /// The bytes of the queue's time that played nothing: the silence it played on past the last buffer before it was
        /// paused, which the position leaves out.
        /// </summary>
        private long _silentBytes;

        public uint SampleRate { get; }
        public uint Channels { get; }
        public uint BitsPerSample { get; }

        public int QueuedFrames => Volatile.Read(ref _queuedFrames);

        public event EventHandler<EventArgs> OnPlaybackCompleted;

        /// <param name="deviceUid">The device's UID, as <see cref="MediaDevices.GetAudioOutputs"/> gives it; null for the default.</param>
        public AudioQueueOutput(string deviceUid, uint sampleRate, uint channels, uint bitsPerSample)
        {
            _deviceUid = deviceUid;
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queue != IntPtr.Zero)
                return;

            // iOS plays nothing of an app whose audio session is not of a category that plays
            if (OperatingSystem.IsIOSVersionAtLeast(14))
                AudioSession.Activate(record: false);

            var format = PcmFormat(SampleRate, Channels, BitsPerSample);
            _self = GCHandle.Alloc(this);
            // of no run loop: the callback comes on a thread of the queue's own
            int status = AudioQueueNewOutput(&format, &OnBufferPlayed, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out _queue);
            if (status != 0)
            {
                _self.Free();
                _queue = IntPtr.Zero;
                throw new InvalidOperationException($"No sound output of {SampleRate} Hz, {Channels} channels, {BitsPerSample} bits: {AppleNative.FourCCString(status)}");
            }
            // of iOS, the system routes the sound: an output is not chosen by an app, so its id is not used
            if (_deviceUid != null && OperatingSystem.IsMacOS())
            {
                status = SetDevice(_queue, _deviceUid);
                if (status != 0)
                    throw new InvalidOperationException($"No sound output of the device {_deviceUid}: {AppleNative.FourCCString(status)}");
            }
        }

        /// <summary>The bytes played since the device was opened or last reset: of the queue's time, which stands still while there is nothing to play.</summary>
        public uint GetPosition()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                long time = QueueTimeBytes();
                if (time >= 0)
                {
                    long position = Math.Min(time - _silentBytes, _enqueuedBytes);
                    _lastPosition = (uint)Math.Max(position, _lastPosition);
                }
                return _lastPosition;
            }
        }

        /// <summary>The queue's time, in bytes; -1 where it has none, not started.</summary>
        private long QueueTimeBytes()
        {
            AudioTimeStamp time;
            if (AudioQueueGetCurrentTime(_queue, IntPtr.Zero, &time, null) != 0 || time.SampleTime < 0)
                return -1;
            return (long)time.SampleTime * Channels * BitsPerSample / 8;
        }

        public void Enqueue(byte[] data, uint length)
        {
            ThrowIfNotInitialized();
            if (length == 0)
                return;
            if (length > data.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            AudioQueueBuffer* buffer = TakeBuffer(length);
            Marshal.Copy(data, 0, (IntPtr)buffer->AudioData, (int)length);
            buffer->AudioDataByteSize = length;

            lock (_lock)
            {
                int status = AudioQueueEnqueueBuffer(_queue, buffer, 0, null);
                if (status != 0)
                {
                    _freeBuffers.Add((IntPtr)buffer);
                    throw new InvalidOperationException($"The sound could not be queued: {AppleNative.FourCCString(status)}");
                }
                Interlocked.Increment(ref _queuedFrames);
                _enqueuedBytes += length;
                if (!_paused && !_running)
                    Start();
            }
        }

        private AudioQueueBuffer* TakeBuffer(uint length)
        {
            // the first free one large enough; those too small are put back
            int tries = _freeBuffers.Count;
            while (tries-- > 0 && _freeBuffers.TryTake(out IntPtr free))
            {
                var buffer = (AudioQueueBuffer*)free;
                if (buffer->AudioDataBytesCapacity >= length)
                    return buffer;
                _freeBuffers.Add(free);
            }

            int status = AudioQueueAllocateBuffer(_queue, Math.Max(length, 16384u), out AudioQueueBuffer* allocated);
            if (status != 0)
                throw new InvalidOperationException($"No buffer for the sound: {AppleNative.FourCCString(status)}");
            return allocated;
        }

        private void Start()
        {
            int status = AudioQueueStart(_queue, null);
            if (status != 0)
                throw new InvalidOperationException($"The sound output did not start: {AppleNative.FourCCString(status)}");
            _running = true;
        }

        /// <summary>Stops playing and lets go of all that is queued; the position starts again from 0.</summary>
        public void Reset()
        {
            ThrowIfNotInitialized();
            lock (_lock)
            {
                // the buffers queued come back through the callback, each raising OnPlaybackCompleted as waveOutReset's do
                AudioQueueStop(_queue, 1);
                _running = false;
                Volatile.Write(ref _queuedFrames, 0);
                _lastPosition = 0;
                _enqueuedBytes = 0;
                _silentBytes = 0;
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
                    AudioQueuePause(_queue);
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
                if (!_running && Volatile.Read(ref _queuedFrames) > 0)
                    Start();
            }
        }

        [UnmanagedCallersOnly]
        private static void OnBufferPlayed(IntPtr userData, IntPtr queue, AudioQueueBuffer* buffer)
        {
            try
            {
                if (GCHandle.FromIntPtr(userData).Target is not AudioQueueOutput output)
                    return;
                output._freeBuffers.Add((IntPtr)buffer);

                // not of the lock: the queue's functions, called under it, may wait for this callback to return
                int queued, left;
                do
                {
                    queued = Volatile.Read(ref output._queuedFrames);
                    left = Math.Max(queued - 1, 0);
                }
                while (Interlocked.CompareExchange(ref output._queuedFrames, left, queued) != queued);

                if (left == 0 && output._running)
                {
                    // nothing more to play: paused, so its time stands still, though not of the queue's own thread
                    ThreadPool.QueueUserWorkItem(static o =>
                    {
                        var self = (AudioQueueOutput)o;
                        lock (self._lock)
                        {
                            if (!self._disposed && Volatile.Read(ref self._queuedFrames) == 0 && self._running)
                            {
                                AudioQueuePause(self._queue);
                                self._running = false;
                                // what it played past the last buffer, before it stopped, was silence
                                long time = self.QueueTimeBytes();
                                if (time - self._silentBytes > self._enqueuedBytes)
                                    self._silentBytes = time - self._enqueuedBytes;
                            }
                        }
                    }, output);
                }

                output.OnPlaybackCompleted?.Invoke(output, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled)
                    Log.Error($"The sound output's callback failed: {ex}");
            }
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queue == IntPtr.Zero)
                throw new InvalidOperationException("The sound output is to be initialized first.");
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            if (_queue != IntPtr.Zero)
            {
                // its buffers go with it
                AudioQueueDispose(_queue, 1);
                _queue = IntPtr.Zero;
            }
            if (_self.IsAllocated)
                _self.Free();
        }
    }
}
