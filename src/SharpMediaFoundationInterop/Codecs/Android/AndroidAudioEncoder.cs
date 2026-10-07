using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.MediaNdk;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of Android's, of MediaCodec through the NDK, of AAC, Opus or FLAC - where the device has one; Android has
    /// no MP3 or ALAC encoder - made for the <see cref="AudioEncoderOptions"/>. It takes what Media Foundation's encoder of
    /// the codec takes - 16 bit PCM of AAC, 16 or 24 bit of FLAC, 32 bit float of Opus - of any length, and hands out a
    /// packet at a time.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AndroidAudioEncoder : IMediaAudioEncoder
    {
        private static readonly int[] AacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        private static readonly Dictionary<AudioCodec, bool> Supported = new Dictionary<AudioCodec, bool>();

        private readonly AudioCodec _codec;
        private readonly AudioEncoderOptions _options;
        private readonly Queue<byte[]> _packets = new Queue<byte[]>();
        private IntPtr _codecHandle;
        private byte[] _config;
        private FlacMetadata _flac;
        private bool _floatIn;
        private long _samplesIn;
        private bool _draining;
        private bool _ended;
        private bool _disposed;

        public AndroidAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (codec is AudioCodec.Mp3 or AudioCodec.Alac)
                throw new NotSupportedException($"Android has no {codec} encoder");
            _codec = codec;
            BitsPerSample = codec == AudioCodec.Opus ? 32 : codec == AudioCodec.Flac ? options.BitsPerSample : 16;
            if (BitsPerSample != 16 && BitsPerSample != 24 && BitsPerSample != 32)
                throw new NotSupportedException($"No {codec} of {BitsPerSample} bits");
        }

        public AudioCodec Codec => _codec;
        public uint Channels => _options.Channels;
        public uint SampleRate => _options.SampleRate;
        public uint BitsPerSample { get; }
        public uint OutputSize => 8192 * Math.Max(Channels, 2);
        public Guid InputFormat => _codec == AudioCodec.Opus ? MediaFormats.Float : MediaFormats.PCM;
        public Guid OutputFormat => MediaFormats.Of(_codec);

        /// <summary>Whether the device has an encoder of the codec: one MediaCodec makes of its type.</summary>
        public static bool Supports(AudioCodec codec)
        {
            if (codec is not (AudioCodec.AAC or AudioCodec.Opus or AudioCodec.Flac) || !MediaNdk.IsAvailable)
                return false;
            lock (Supported)
            {
                if (!Supported.TryGetValue(codec, out bool supported))
                {
                    IntPtr probe = AMediaCodec_createEncoderByType(AndroidAudioDecoder.Mime(codec));
                    supported = probe != IntPtr.Zero;
                    if (supported)
                        AMediaCodec_delete(probe);
                    Supported[codec] = supported;
                }
                return supported;
            }
        }

        /// <summary>
        /// The codec's configuration, for the container to carry: of AAC the AudioSpecificConfig, known once initialized; of
        /// FLAC the metadata blocks, each with its header, known once the first frame is read out and complete, of the total
        /// samples and MD5, once drained; of Opus null, as of Windows.
        /// </summary>
        public byte[] Config => _codec switch
        {
            AudioCodec.AAC => _config,
            AudioCodec.Flac => _flac?.ToConfig(),
            _ => null
        };

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_codecHandle != IntPtr.Zero)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"This device has no {_codec} encoder");

            IntPtr format = AMediaFormat_new();
            try
            {
                AMediaFormat_setString(format, "mime", AndroidAudioDecoder.Mime(_codec));
                AMediaFormat_setInt32(format, "sample-rate", (int)SampleRate);
                AMediaFormat_setInt32(format, "channel-count", (int)Channels);
                // 24 bit PCM goes in as float, which holds it exactly; Opus's float as 16 bit, which every encoder takes
                _floatIn = BitsPerSample == 24;
                AMediaFormat_setInt32(format, "pcm-encoding", _floatIn ? ENCODING_PCM_FLOAT : ENCODING_PCM_16BIT);
                AMediaFormat_setInt32(format, "max-input-size", 65536);
                if (_codec == AudioCodec.AAC)
                {
                    AMediaFormat_setInt32(format, "aac-profile", 2); // AAC-LC
                    AMediaFormat_setInt32(format, "bitrate", (int)(64000 * Channels));
                }
                else if (_codec == AudioCodec.Opus)
                {
                    AMediaFormat_setInt32(format, "bitrate", (int)(48000 * Channels));
                }
                else
                {
                    AMediaFormat_setInt32(format, "flac-compression-level", 5);
                }

                _codecHandle = AMediaCodec_createEncoderByType(AndroidAudioDecoder.Mime(_codec));
                int status = _codecHandle == IntPtr.Zero ? -1 : AMediaCodec_configure(_codecHandle, format, IntPtr.Zero, IntPtr.Zero, CONFIGURE_FLAG_ENCODE);
                if (status == AMEDIA_OK)
                    status = AMediaCodec_start(_codecHandle);
                if (status != AMEDIA_OK)
                {
                    if (_codecHandle != IntPtr.Zero)
                        AMediaCodec_delete(_codecHandle);
                    _codecHandle = IntPtr.Zero;
                    throw new NotSupportedException($"The {_codec} encoder takes no {SampleRate} Hz, {Channels} channels, {BitsPerSample} bits: {status}");
                }
            }
            finally
            {
                AMediaFormat_delete(format);
            }

            // AAC-LC's AudioSpecificConfig, known now - the encoder's own replaces it as it comes
            if (_codec == AudioCodec.AAC && Array.IndexOf(AacRates, (int)SampleRate) is int index && index >= 0)
            {
                int value = (2 << 11) | (index << 7) | ((int)(Channels == 8 ? 7 : Channels) << 3);
                _config = [(byte)(value >> 8), (byte)value];
            }
            if (_codec == AudioCodec.Flac)
                _flac = new FlacMetadata((int)(Channels * BitsPerSample / 8));
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>PCM in, of any length: into the encoder's input buffers as it has room.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            _flac?.AddInput(data);
            byte[] pcm = Convert(data);
            int frameBytes = (int)Channels * (_floatIn ? 4 : 2);
            int offset = 0;
            while (offset < pcm.Length)
            {
                var stopwatch = Stopwatch.StartNew();
                nint index;
                while ((index = AMediaCodec_dequeueInputBuffer(_codecHandle, 5000)) < 0)
                {
                    Pump();
                    if (stopwatch.ElapsedMilliseconds > 5000)
                        return false;
                }
                byte* buffer = AMediaCodec_getInputBuffer(_codecHandle, (nuint)index, out nuint capacity);
                int length = Math.Min(pcm.Length - offset, (int)capacity / frameBytes * frameBytes);
                pcm.AsSpan(offset, length).CopyTo(new Span<byte>(buffer, length));
                long timeUs = _samplesIn * 1_000_000 / SampleRate;
                AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, (nuint)length, (ulong)timeUs, 0);
                _samplesIn += length / frameBytes;
                offset += length;
                Pump();
            }
            return true;
        }

        /// <summary>The PCM as the encoder takes it: 24 bit as float, float as 16 bit; 16 bit as it is.</summary>
        private byte[] Convert(ReadOnlySpan<byte> data)
        {
            if (_floatIn)
            {
                int count = data.Length / 3;
                var result = new byte[count * 4];
                var samples = MemoryMarshal.Cast<byte, float>(result.AsSpan());
                for (int i = 0; i < count; i++)
                    samples[i] = ((data[i * 3] | (data[i * 3 + 1] << 8) | ((sbyte)data[i * 3 + 2] << 16))) / 8388608f;
                return result;
            }
            if (BitsPerSample == 32)
            {
                var floats = MemoryMarshal.Cast<byte, float>(data);
                var result = new byte[floats.Length * 2];
                var samples = MemoryMarshal.Cast<byte, short>(result.AsSpan());
                for (int i = 0; i < floats.Length; i++)
                    samples[i] = (short)Math.Clamp(MathF.Round(floats[i] * 32767f), -32768f, 32767f);
                return result;
            }
            return data.ToArray();
        }

        /// <summary>
        /// Takes every packet the encoder has made: of its codec configuration - AAC's AudioSpecificConfig, FLAC's header,
        /// Opus's OpusHead - kept, not handed out.
        /// </summary>
        private void Pump()
        {
            AMediaCodecBufferInfo info;
            while (true)
            {
                nint index = AMediaCodec_dequeueOutputBuffer(_codecHandle, &info, 0);
                if (index == INFO_TRY_AGAIN_LATER)
                    break;
                if (index < 0)
                    continue;
                if ((info.Flags & BUFFER_FLAG_END_OF_STREAM) != 0)
                    _ended = true;
                if (info.Size > 0)
                {
                    byte* data = AMediaCodec_getOutputBuffer(_codecHandle, (nuint)index, out _);
                    var payload = new ReadOnlySpan<byte>(data + info.Offset, info.Size);
                    if ((info.Flags & BUFFER_FLAG_CODEC_CONFIG) != 0)
                    {
                        if (_codec == AudioCodec.AAC)
                            _config = payload.ToArray();
                        else if (_codec == AudioCodec.Flac)
                            _flac.AddHeader(payload);
                    }
                    else
                    {
                        _flac?.AddFrame(payload.Length);
                        _packets.Enqueue(payload.ToArray());
                    }
                }
                AMediaCodec_releaseOutputBuffer(_codecHandle, (nuint)index, false);
            }
        }

        /// <summary>The next packet. While draining it waits for one, up to the stream's end.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            ThrowIfNotInitialized();
            Pump();
            if (_draining)
            {
                var stopwatch = Stopwatch.StartNew();
                while (_packets.Count == 0 && !_ended && stopwatch.ElapsedMilliseconds < 10000)
                {
                    Thread.Sleep(2);
                    Pump();
                }
            }
            if (!_packets.TryDequeue(out var packet))
            {
                length = 0;
                return false;
            }
            if (buffer == null || buffer.Length < packet.Length)
                buffer = new byte[Math.Max(packet.Length, OutputSize)];
            Buffer.BlockCopy(packet, 0, buffer, 0, packet.Length);
            length = (uint)packet.Length;
            return true;
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Ends the stream, so that everything the encoder holds comes out - the last packet padded.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _draining = true;
            _flac?.End();
            var stopwatch = Stopwatch.StartNew();
            nint index;
            while ((index = AMediaCodec_dequeueInputBuffer(_codecHandle, 5000)) < 0 && stopwatch.ElapsedMilliseconds < 5000)
                Pump();
            if (index >= 0)
                AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, 0, (ulong)(_samplesIn * 1_000_000 / SampleRate), BUFFER_FLAG_END_OF_STREAM);
        }

        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _ended = false;
            AMediaCodec_flush(_codecHandle);
        }

        public void Flush()
        {
            ThrowIfNotInitialized();
            AMediaCodec_flush(_codecHandle);
            _packets.Clear();
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_codecHandle == IntPtr.Zero)
                throw new InvalidOperationException("The encoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_codecHandle != IntPtr.Zero)
            {
                AMediaCodec_stop(_codecHandle);
                AMediaCodec_delete(_codecHandle);
                _codecHandle = IntPtr.Zero;
            }
            _flac?.Dispose();
        }
    }
}
