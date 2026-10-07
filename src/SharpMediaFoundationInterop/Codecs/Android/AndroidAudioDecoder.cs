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
    /// A decoder of Android's, of MediaCodec through the NDK, of any <see cref="AudioCodec"/> the device has a decoder of,
    /// made for the <see cref="AudioDecoderOptions"/>: PCM out, of the format Media Foundation's decoder of the codec hands
    /// out - 16 bit of AAC and MP3, the stream's 16 or 24 of FLAC and ALAC, 32 bit float of Opus - interleaved, of more than
    /// two channels in WAVE's order, which is Android's own. Opus is of 48 kHz on Android, whatever was asked.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AndroidAudioDecoder : IMediaAudioTransform
    {
        private static readonly Dictionary<AudioCodec, bool> Supported = new Dictionary<AudioCodec, bool>();

        private readonly AudioCodec _codec;
        private readonly AudioDecoderOptions _options;
        private readonly Queue<byte[]> _decoded = new Queue<byte[]>();
        private readonly uint _skipSamples;
        private uint _skipBytes;
        private uint _maxFrames;
        private IntPtr _codecHandle;
        private bool _floatOut;
        /// <summary>The PCM the decoder hands out, of its output format: what was asked of it, or what it would rather.</summary>
        private int _encoding = ENCODING_PCM_16BIT;
        private bool _draining;
        private bool _ended;
        private bool _disposed;

        public AndroidAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _codec = codec;
            _skipSamples = options.SkipSamples;
            Channels = options.Channels;
            SampleRate = options.SampleRate;
            BitsPerSample = 16;
            switch (codec)
            {
                case AudioCodec.AAC:
                    if (options.Config == null)
                        throw new ArgumentException("AAC needs its AudioSpecificConfig", nameof(options));
                    _maxFrames = 2048 * 2;
                    break;
                case AudioCodec.Mp3:
                    _maxFrames = 1152 * 2;
                    break;
                case AudioCodec.Opus:
                    SampleRate = 48000;
                    BitsPerSample = 32;
                    _maxFrames = 5760;
                    break;
                case AudioCodec.Flac:
                    var blocks = options.Config ?? throw new ArgumentException("FLAC needs its metadata blocks", nameof(options));
                    if (blocks.Length < 4 + 34 || (blocks[0] & 0x7F) != 0)
                        throw new ArgumentException("FLAC's metadata blocks begin with STREAMINFO.", nameof(options));
                    var s = blocks.AsSpan(4);
                    _maxFrames = (uint)((s[2] << 8) | s[3]);
                    SampleRate = (uint)((s[10] << 12) | (s[11] << 4) | (s[12] >> 4));
                    Channels = (uint)(((s[12] >> 1) & 0x7) + 1);
                    BitsPerSample = (uint)((((s[12] & 0x1) << 4) | (s[13] >> 4)) + 1) > 16 ? 24u : 16u;
                    break;
                case AudioCodec.Alac:
                    var config = options.Config ?? throw new ArgumentException("ALAC needs its ALACSpecificConfig", nameof(options));
                    var c = config.AsSpan(config.Length - 24);
                    _maxFrames = (uint)((c[0] << 24) | (c[1] << 16) | (c[2] << 8) | c[3]);
                    BitsPerSample = c[5] > 16 ? 24u : 16u;
                    Channels = c[9];
                    SampleRate = (uint)((c[20] << 24) | (c[21] << 16) | (c[22] << 8) | c[23]);
                    break;
                default:
                    throw new NotSupportedException($"No MediaCodec decoder of {codec}");
            }
        }

        public AudioCodec Codec => _codec;
        public uint Channels { get; }
        public uint SampleRate { get; }
        public uint BitsPerSample { get; }
        public uint OutputSize => _maxFrames * Channels * BitsPerSample / 8;
        public Guid InputFormat => MediaFormats.Of(_codec);
        public Guid OutputFormat => _codec == AudioCodec.Opus ? MediaFormats.Float : MediaFormats.PCM;

        internal static string Mime(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC => "audio/mp4a-latm",
            AudioCodec.Mp3 => "audio/mpeg",
            AudioCodec.Opus => "audio/opus",
            AudioCodec.Flac => "audio/flac",
            AudioCodec.Alac => "audio/alac",
            _ => throw new NotSupportedException($"No MediaCodec type of {codec}")
        };

        /// <summary>Whether the device has a decoder of the codec: one MediaCodec makes of its type.</summary>
        public static bool Supports(AudioCodec codec)
        {
            if (!Enum.IsDefined(codec) || !MediaNdk.IsAvailable)
                return false;
            lock (Supported)
            {
                if (!Supported.TryGetValue(codec, out bool supported))
                {
                    IntPtr probe = AMediaCodec_createDecoderByType(Mime(codec));
                    supported = probe != IntPtr.Zero;
                    if (supported)
                        AMediaCodec_delete(probe);
                    Supported[codec] = supported;
                }
                return supported;
            }
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_codecHandle != IntPtr.Zero)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"This device has no {_codec} decoder");

            IntPtr format = AMediaFormat_new();
            try
            {
                AMediaFormat_setString(format, "mime", Mime(_codec));
                AMediaFormat_setInt32(format, "sample-rate", (int)(_codec == AudioCodec.Opus ? 48000 : SampleRate));
                AMediaFormat_setInt32(format, "channel-count", (int)Channels);
                // 24 bit PCM as float, which holds every 24 bit sample exactly, and is turned back: not every decoder has 24 bit out
                _floatOut = _codec == AudioCodec.Opus || BitsPerSample == 24;
                _encoding = _floatOut ? ENCODING_PCM_FLOAT : ENCODING_PCM_16BIT;
                AMediaFormat_setInt32(format, "pcm-encoding", _encoding);
                switch (_codec)
                {
                    case AudioCodec.AAC:
                        SetBuffer(format, "csd-0", _options.Config);
                        break;
                    case AudioCodec.Opus:
                        // the decoder needs all three: its header, its delay - skipped here, as Windows' - and its seek preroll
                        SetBuffer(format, "csd-0", OpusHead());
                        SetBuffer(format, "csd-1", BitConverter.GetBytes(0L));
                        SetBuffer(format, "csd-2", BitConverter.GetBytes(80_000_000L));
                        break;
                    case AudioCodec.Flac:
                        var header = new byte[4 + _options.Config.Length];
                        "fLaC"u8.CopyTo(header);
                        _options.Config.CopyTo(header, 4);
                        SetBuffer(format, "csd-0", header);
                        break;
                    case AudioCodec.Alac:
                        SetBuffer(format, "csd-0", _options.Config.AsSpan(_options.Config.Length - 24));
                        break;
                }

                _codecHandle = AMediaCodec_createDecoderByType(Mime(_codec));
                int status = _codecHandle == IntPtr.Zero ? -1 : AMediaCodec_configure(_codecHandle, format, IntPtr.Zero, IntPtr.Zero, 0);
                if (status == AMEDIA_OK)
                    status = AMediaCodec_start(_codecHandle);
                if (status != AMEDIA_OK)
                {
                    if (_codecHandle != IntPtr.Zero)
                        AMediaCodec_delete(_codecHandle);
                    _codecHandle = IntPtr.Zero;
                    throw new NotSupportedException($"The {_codec} decoder takes no stream of this configuration: {status}");
                }
            }
            finally
            {
                AMediaFormat_delete(format);
            }
            _skipBytes = SkipBytes;
        }

        /// <summary>An OpusHead (RFC 7845 5.1) of the channels: of mapping family 0, of one or two channels.</summary>
        private byte[] OpusHead()
        {
            var head = new byte[19];
            "OpusHead"u8.CopyTo(head);
            head[8] = 1;
            head[9] = (byte)Channels;
            BitConverter.TryWriteBytes(head.AsSpan(12), _options.SampleRate);
            return head;
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One packet in, decoded as the decoder has room for it.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            return Queue(data, timestamp, 0);
        }

        private bool Queue(ReadOnlySpan<byte> data, long time, uint flags)
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
            int length = (int)Math.Min((nuint)data.Length, capacity);
            data.Slice(0, length).CopyTo(new Span<byte>(buffer, length));
            AMediaCodec_queueInputBuffer(_codecHandle, (nuint)index, 0, (nuint)length, (ulong)Math.Max(time / 10, 0), flags);
            Pump();
            return true;
        }

        /// <summary>Takes every buffer of PCM the decoder has: of float, turned to 24 bit where that is what is handed out.</summary>
        private void Pump()
        {
            AMediaCodecBufferInfo info;
            while (true)
            {
                nint index = AMediaCodec_dequeueOutputBuffer(_codecHandle, &info, 0);
                if (index == INFO_TRY_AGAIN_LATER)
                    break;
                if (index == INFO_OUTPUT_FORMAT_CHANGED)
                {
                    // a decoder may hand out other PCM than was asked of it: 16 bit, of no float of its own
                    IntPtr output = AMediaCodec_getOutputFormat(_codecHandle);
                    _encoding = AMediaFormat_getInt32(output, "pcm-encoding", out int encoding) ? encoding : ENCODING_PCM_16BIT;
                    AMediaFormat_delete(output);
                    continue;
                }
                if (index < 0)
                    continue;
                if ((info.Flags & BUFFER_FLAG_END_OF_STREAM) != 0)
                    _ended = true;
                if (info.Size > 0 && (info.Flags & BUFFER_FLAG_CODEC_CONFIG) == 0)
                {
                    byte* data = AMediaCodec_getOutputBuffer(_codecHandle, (nuint)index, out _);
                    _decoded.Enqueue(ToOutput(new ReadOnlySpan<byte>(data + info.Offset, info.Size)));
                }
                AMediaCodec_releaseOutputBuffer(_codecHandle, (nuint)index, false);
            }
        }

        /// <summary>The decoder's PCM as this hands it out: of the bits of <see cref="BitsPerSample"/>, float of 32.</summary>
        private byte[] ToOutput(ReadOnlySpan<byte> pcm)
        {
            int outBits = (int)BitsPerSample;
            switch (_encoding)
            {
                case ENCODING_PCM_FLOAT:
                    return outBits == 32 ? pcm.ToArray() : outBits == 24 ? FloatTo24(pcm) : FloatTo16(pcm);
                case ENCODING_PCM_24BIT_PACKED:
                    return outBits == 24 ? pcm.ToArray() : throw new NotSupportedException("The decoder hands out 24 bit PCM where other was asked");
                default:
                    if (outBits == 16)
                        return pcm.ToArray();
                    // 16 bit where more was asked: widened, of no more precision
                    var samples = MemoryMarshal.Cast<byte, short>(pcm);
                    var result = new byte[samples.Length * outBits / 8];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        if (outBits == 32)
                        {
                            BitConverter.TryWriteBytes(result.AsSpan(i * 4), samples[i] / 32768f);
                        }
                        else
                        {
                            int value = samples[i] << 8;
                            result[i * 3] = (byte)value;
                            result[i * 3 + 1] = (byte)(value >> 8);
                            result[i * 3 + 2] = (byte)(value >> 16);
                        }
                    }
                    return result;
            }
        }

        private static byte[] FloatTo16(ReadOnlySpan<byte> pcm)
        {
            var samples = MemoryMarshal.Cast<byte, float>(pcm);
            var result = new byte[samples.Length * 2];
            var output = MemoryMarshal.Cast<byte, short>(result.AsSpan());
            for (int i = 0; i < samples.Length; i++)
                output[i] = (short)Math.Clamp(MathF.Round(samples[i] * 32768f), -32768f, 32767f);
            return result;
        }

        /// <summary>32 bit float to packed 24 bit PCM: exact, of samples that were of 24 bits.</summary>
        private static byte[] FloatTo24(ReadOnlySpan<byte> pcm)
        {
            var samples = MemoryMarshal.Cast<byte, float>(pcm);
            var result = new byte[samples.Length * 3];
            for (int i = 0; i < samples.Length; i++)
            {
                int value = (int)Math.Clamp(MathF.Round(samples[i] * 8388608f), -8388608f, 8388607f);
                result[i * 3] = (byte)value;
                result[i * 3 + 1] = (byte)(value >> 8);
                result[i * 3 + 2] = (byte)(value >> 16);
            }
            return result;
        }

        /// <summary>
        /// The next sound out, past what is to be skipped. While draining it waits for one, up to the stream's end. The
        /// buffer is made larger where it is too small.
        /// </summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            ThrowIfNotInitialized();
            while (true)
            {
                Pump();
                if (_draining)
                {
                    var stopwatch = Stopwatch.StartNew();
                    while (_decoded.Count == 0 && !_ended && stopwatch.ElapsedMilliseconds < 10000)
                    {
                        Thread.Sleep(2);
                        Pump();
                    }
                }
                if (!_decoded.TryDequeue(out var pcm))
                {
                    length = 0;
                    return false;
                }
                uint skip = Math.Min(_skipBytes, (uint)pcm.Length);
                _skipBytes -= skip;
                length = (uint)pcm.Length - skip;
                if (length == 0)
                    continue;
                if (buffer == null || buffer.Length < length)
                    buffer = new byte[Math.Max(length, OutputSize)];
                Buffer.BlockCopy(pcm, (int)skip, buffer, 0, (int)length);
                return true;
            }
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _draining = true;
            Queue(ReadOnlySpan<byte>.Empty, 0, BUFFER_FLAG_END_OF_STREAM);
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
            _decoded.Clear();
            _skipBytes = SkipBytes;
        }

        private uint SkipBytes => _skipSamples * Channels * (BitsPerSample / 8);

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_codecHandle == IntPtr.Zero)
                throw new InvalidOperationException("The decoder is to be initialized first.");
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
        }
    }
}
