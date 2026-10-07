using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AudioQueue;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Records PCM on macOS and iOS, of an AudioQueue: from the device of the id given - on macOS a Core Audio device's UID,
    /// on iOS an audio session input's - or the system's default, in buffers of a second each, as waveIn's are. The first
    /// time, the user is asked whether the app may use the microphone: on macOS, until they say so, what is recorded is
    /// silence; on iOS <see cref="Initialize"/> waits for their answer, and throws <see cref="UnauthorizedAccessException"/>
    /// where they say no.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    public sealed unsafe class AudioQueueInput : IAudioInput
    {
        private const int BufferCount = 3;

        private readonly string _deviceUid;
        private IntPtr _queue;
        private GCHandle _self;
        private volatile bool _running;
        private bool _disposed;

        public uint SampleRate { get; }
        public uint Channels { get; }
        public uint BitsPerSample { get; }

        public event EventHandler<AudioInputEventArgs> FrameReceived;

        /// <param name="deviceUid">The device's UID, as <see cref="MediaDevices.GetAudioInputs"/> gives it; null for the default.</param>
        public AudioQueueInput(string deviceUid, uint sampleRate, uint channels, uint bitsPerSample)
        {
            _deviceUid = deviceUid;
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        /// <summary>Opens the device and starts recording.</summary>
        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queue == IntPtr.Zero)
            {
                if (OperatingSystem.IsIOSVersionAtLeast(14))
                {
                    // iOS records only of PlayAndRecord, and only of the user's leave
                    AudioSession.Activate(record: true);
                    AudioSession.RequestRecordPermission();
                    if (_deviceUid != null && !AudioSession.SetPreferredInput(_deviceUid))
                        throw new InvalidOperationException($"No sound input of the device {_deviceUid}");
                }

                var format = PcmFormat(SampleRate, Channels, BitsPerSample);
                _self = GCHandle.Alloc(this);
                int status = AudioQueueNewInput(&format, &OnBufferRecorded, GCHandle.ToIntPtr(_self), IntPtr.Zero, IntPtr.Zero, 0, out _queue);
                if (status != 0)
                {
                    _self.Free();
                    _queue = IntPtr.Zero;
                    throw new InvalidOperationException($"No sound input of {SampleRate} Hz, {Channels} channels, {BitsPerSample} bits: {AppleNative.FourCCString(status)}");
                }
                if (_deviceUid != null && OperatingSystem.IsMacOS())
                {
                    status = SetDevice(_queue, _deviceUid);
                    if (status != 0)
                        throw new InvalidOperationException($"No sound input of the device {_deviceUid}: {AppleNative.FourCCString(status)}");
                }
            }

            if (_running)
                return;
            uint size = SampleRate * Channels * BitsPerSample / 8;
            for (int i = 0; i < BufferCount; i++)
            {
                if (AudioQueueAllocateBuffer(_queue, size, out AudioQueueBuffer* buffer) == 0)
                    AudioQueueEnqueueBuffer(_queue, buffer, 0, null);
            }
            _running = true;
            int started = AudioQueueStart(_queue, null);
            if (started != 0)
            {
                _running = false;
                throw new InvalidOperationException($"The sound input did not start: {AppleNative.FourCCString(started)}");
            }
        }

        /// <summary>Stops recording; the buffers not yet full are let go of.</summary>
        public void Reset()
        {
            if (_queue == IntPtr.Zero || !_running)
                return;
            _running = false;
            AudioQueueStop(_queue, 1);
        }

        [UnmanagedCallersOnly]
        private static void OnBufferRecorded(IntPtr userData, IntPtr queue, AudioQueueBuffer* buffer, AudioTimeStamp* startTime, uint packets, AudioStreamPacketDescription* descriptions)
        {
            try
            {
                if (GCHandle.FromIntPtr(userData).Target is not AudioQueueInput input)
                    return;

                if (buffer->AudioDataByteSize > 0 && input._running)
                {
                    var data = new byte[buffer->AudioDataByteSize];
                    Marshal.Copy((IntPtr)buffer->AudioData, data, 0, data.Length);
                    input.FrameReceived?.Invoke(input, new AudioInputEventArgs(data));
                }

                // the buffer goes back to be filled again, while recording
                if (input._running)
                    AudioQueueEnqueueBuffer(queue, buffer, 0, null);
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled)
                    Log.Error($"The sound input's callback failed: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _running = false;
            if (_queue != IntPtr.Zero)
            {
                AudioQueueDispose(_queue, 1);
                _queue = IntPtr.Zero;
            }
            if (_self.IsAllocated)
                _self.Free();
        }
    }
}
