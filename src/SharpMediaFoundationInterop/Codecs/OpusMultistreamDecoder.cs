using System;
using System.Buffers.Binary;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// Opus of more than two channels, or of more than one stream (RFC 7845 5.1.1): a packet is a stream's packet after
    /// another, each but the last self-delimited (RFC 6716 Appendix B), each decoded by a decoder of the platform's - of one
    /// channel, or of two of a coupled stream - which needs no more than mono and stereo, as Windows' is. The channels are
    /// put out as the channel mapping says: of family 1 in WAVE's order rather than Vorbis', as every other decoder hands
    /// them out; of family 2, ambisonics, in ACN order, as they are; of family 255 as they are. Family 3's demixing is not
    /// done. 32 bit float out, of 48 kHz.
    /// </summary>
    public sealed class OpusMultistreamDecoder : IMediaAudioTransform
    {
        /// <summary>The longest packet's samples of a channel: 120 ms (RFC 6716 3.2.5).</summary>
        private const int MaxPacketSamples = 5760;

        private readonly IMediaAudioTransform[] _streams;
        private readonly int _coupled;

        /// <summary>Of each channel out: of which stream, and of which of its channels; a stream of -1 is silent.</summary>
        private readonly (int Stream, int Channel)[] _sources;

        // of each stream, what its decoder handed out and is not yet put out: interleaved floats of its channels
        private readonly float[][] _decoded;
        private readonly int[] _decodedCount;
        private byte[] _streamBuffer;

        public OpusMultistreamDecoder(AudioDecoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var head = ReadHead(options.Config) ?? throw new ArgumentException("Opus of more than two channels needs its 'dOps'", nameof(options));
            if (head.Family == 3)
                throw new NotSupportedException("Opus of channel mapping family 3: ambisonics of a demixing matrix");

            Channels = (uint)head.Mapping.Length;
            _coupled = head.Coupled;

            // Vorbis' order of each count of channels, of the WAVE channel each is: what is put out at WAVE's i is Vorbis' order[i]
            int[] order = head.Family == 1 ? VorbisToWave(head.Mapping.Length) : null;
            _sources = new (int, int)[head.Mapping.Length];
            for (int i = 0; i < _sources.Length; i++)
            {
                int index = head.Mapping[order == null ? i : order[i]];
                _sources[i] = index == 255 ? (-1, 0)
                    : index < 2 * head.Coupled ? (index / 2, index % 2)
                    : (index - head.Coupled, 0);
                if (_sources[i].Item1 >= head.Streams)
                    throw new ArgumentException($"Opus channel {i} is of stream {_sources[i].Item1} of {head.Streams}", nameof(options));
            }

            _streams = new IMediaAudioTransform[head.Streams];
            _decoded = new float[head.Streams][];
            _decodedCount = new int[head.Streams];
            try
            {
                for (int s = 0; s < _streams.Length; s++)
                {
                    uint channels = s < head.Coupled ? 2u : 1u;
                    _streams[s] = MediaCodecs.CreateAudioDecoder(AudioCodec.Opus, new AudioDecoderOptions
                    {
                        Channels = channels,
                        SampleRate = options.SampleRate,
                        SkipSamples = options.SkipSamples,
                    });
                    _decoded[s] = new float[MaxPacketSamples * 4 * channels];
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Whether a stream of the options is of more than one Opus stream, or channel mapping family, for this decoder.</summary>
        public static bool IsMultistream(AudioDecoderOptions options)
        {
            var head = ReadHead(options?.Config);
            return head != null ? head.Value.Family != 0 || head.Value.Streams != 1 : options?.Channels > 2;
        }

        /// <summary>
        /// The channel mapping of a 'dOps' box's payload (Opus in ISOBMFF 4.3.2): version, the channels, the pre-skip, the
        /// input rate and the gain, then the family, and of a family other than 0 the streams, the coupled ones and the
        /// mapping. Of family 0 one stream, coupled of two channels. Null where there is none.
        /// </summary>
        private static (int Family, int Streams, int Coupled, byte[] Mapping)? ReadHead(byte[] config)
        {
            if (config == null || config.Length < 11)
                return null;
            int channels = config[1], family = config[10];
            if (family == 0)
                return (0, 1, channels == 2 ? 1 : 0, channels == 2 ? new byte[] { 0, 1 } : new byte[] { 0 });
            if (config.Length < 13 + channels)
                return null;
            return (family, config[11], config[12], config.AsSpan(13, channels).ToArray());
        }

        /// <summary>
        /// Of family 1's 3 to 8 channels (RFC 7845 5.1.1.2), the Vorbis channel of each WAVE channel, of the speakers
        /// <see cref="Output.SpeakerLayout.ChannelMask"/> says. Fewer, or more, as they are.
        /// </summary>
        private static int[] VorbisToWave(int channels) => channels switch
        {
            3 => new[] { 0, 2, 1 },                   // L C R
            4 => new[] { 0, 1, 2, 3 },                // FL FR RL RR
            5 => new[] { 0, 2, 1, 3, 4 },             // FL C FR RL RR
            6 => new[] { 0, 2, 1, 5, 3, 4 },          // FL C FR RL RR LFE
            7 => new[] { 0, 2, 1, 6, 3, 4, 5 },       // FL C FR SL SR RC LFE
            8 => new[] { 0, 2, 1, 7, 5, 6, 3, 4 },    // FL C FR SL SR RL RR LFE
            _ => null
        };

        public uint Channels { get; }
        public uint SampleRate => 48000;
        public uint BitsPerSample => 32;

        public Guid InputFormat => _streams[0].InputFormat;
        public Guid OutputFormat => MediaFormats.Float;

        /// <summary>The longest packet's: more is handed out in a buffer grown for it.</summary>
        public uint OutputSize => MaxPacketSamples * Channels * 4;

        public void Initialize()
        {
            foreach (var stream in _streams)
                stream.Initialize();
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput((ReadOnlySpan<byte>)data, timestamp);

        /// <summary>Each stream's packet to its decoder; what they hand out is kept, to be put out together.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            bool taken = true;
            for (int s = 0; s < _streams.Length; s++)
            {
                ReadOnlySpan<byte> packet;
                if (s == _streams.Length - 1)
                {
                    packet = data;
                    data = default;
                }
                else
                {
                    int length = SelfDelimited(data, out byte[] undelimited);
                    if (length <= 0)
                        return false; // not of the streams it says: dropped
                    packet = undelimited;
                    data = data.Slice(length);
                }
                taken &= _streams[s].ProcessInput(packet, timestamp);
            }
            TakeDecoded();
            return taken;
        }

        /// <summary>
        /// A self-delimited packet's length, and the packet as it would be undelimited: the length of its last frame, which
        /// its framing does not say otherwise, taken out (RFC 6716 B.2). 0 where it is not one.
        /// </summary>
        private static int SelfDelimited(ReadOnlySpan<byte> data, out byte[] packet)
        {
            packet = null;
            if (data.Length < 2)
                return 0;
            int code = data[0] & 3;
            int at = 1, frames, padding = 0;
            int lengthsEnd; // where the length normal framing has not, the last frame's, starts
            if (code == 3)
            {
                int count = data[at++];
                frames = count & 0x3F;
                bool vbr = (count & 0x80) != 0;
                if ((count & 0x40) != 0)
                {
                    int p;
                    do
                    {
                        if (at >= data.Length) return 0;
                        p = data[at++];
                        padding += p == 255 ? 254 : p;
                    } while (p == 255);
                }
                int sum = 0;
                if (vbr)
                {
                    for (int f = 0; f < frames - 1; f++)
                    {
                        int l = ReadLength(data, ref at);
                        if (l < 0) return 0;
                        sum += l;
                    }
                }
                lengthsEnd = at;
                int last = ReadLength(data, ref at);
                if (last < 0 || frames == 0) return 0;
                int total = vbr ? sum + last : last * frames;
                return Undelimit(data, lengthsEnd, at, total + padding, out packet);
            }
            else
            {
                int total;
                if (code == 2)
                {
                    int first = ReadLength(data, ref at);
                    lengthsEnd = at;
                    int second = ReadLength(data, ref at);
                    if (first < 0 || second < 0) return 0;
                    total = first + second;
                }
                else
                {
                    lengthsEnd = at;
                    int length = ReadLength(data, ref at);
                    if (length < 0) return 0;
                    total = code == 1 ? 2 * length : length;
                }
                return Undelimit(data, lengthsEnd, at, total, out packet);
            }
        }

        /// <summary>The packet without the length between <paramref name="added"/> and <paramref name="body"/>, and the length of all of it.</summary>
        private static int Undelimit(ReadOnlySpan<byte> data, int added, int body, int bodyLength, out byte[] packet)
        {
            packet = null;
            if (body + bodyLength > data.Length)
                return 0;
            packet = new byte[added + bodyLength];
            data.Slice(0, added).CopyTo(packet);
            data.Slice(body, bodyLength).CopyTo(packet.AsSpan(added));
            return body + bodyLength;
        }

        /// <summary>A frame's length (RFC 6716 3.2.1): of one byte below 252, else of two. -1 past the end.</summary>
        private static int ReadLength(ReadOnlySpan<byte> data, ref int at)
        {
            if (at >= data.Length)
                return -1;
            int first = data[at++];
            if (first < 252)
                return first;
            if (at >= data.Length)
                return -1;
            return first + 4 * data[at++];
        }

        /// <summary>What each stream's decoder hands out, kept until every stream has handed out as much.</summary>
        private void TakeDecoded()
        {
            _streamBuffer ??= new byte[MaxPacketSamples * 2 * 4];
            for (int s = 0; s < _streams.Length; s++)
            {
                while (_streams[s].ProcessOutput(ref _streamBuffer, out uint length))
                {
                    int floats = (int)length / 4;
                    if (_decodedCount[s] + floats > _decoded[s].Length)
                        Array.Resize(ref _decoded[s], Math.Max(_decodedCount[s] + floats, _decoded[s].Length * 2));
                    Buffer.BlockCopy(_streamBuffer, 0, _decoded[s], _decodedCount[s] * 4, floats * 4);
                    _decodedCount[s] += floats;
                }
            }
        }

        /// <summary>As many samples as every stream has handed out, of each channel, as the mapping puts them.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            length = 0;
            int samples = int.MaxValue;
            for (int s = 0; s < _streams.Length; s++)
                samples = Math.Min(samples, _decodedCount[s] / StreamChannels(s));
            if (samples == 0 || samples == int.MaxValue)
                return false;

            int channels = (int)Channels;
            int bytes = samples * channels * 4;
            if (buffer == null || buffer.Length < bytes)
                buffer = new byte[bytes];
            var output = buffer.AsSpan();
            for (int c = 0; c < channels; c++)
            {
                var (stream, channel) = _sources[c];
                if (stream < 0)
                {
                    for (int i = 0; i < samples; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(output.Slice((i * channels + c) * 4), 0f);
                    continue;
                }
                int stride = StreamChannels(stream);
                var decoded = _decoded[stream];
                for (int i = 0; i < samples; i++)
                    BinaryPrimitives.WriteSingleLittleEndian(output.Slice((i * channels + c) * 4), decoded[i * stride + channel]);
            }

            for (int s = 0; s < _streams.Length; s++)
            {
                int used = samples * StreamChannels(s);
                _decodedCount[s] -= used;
                Array.Copy(_decoded[s], used, _decoded[s], 0, _decodedCount[s]);
            }
            length = (uint)bytes;
            return true;
        }

        private int StreamChannels(int stream) => stream < _coupled ? 2 : 1;

        public bool Drain()
        {
            bool drained = true;
            foreach (var stream in _streams)
                drained &= stream.Drain();
            return drained;
        }

        public void BeginDrain()
        {
            foreach (var stream in _streams)
                stream.BeginDrain();
            TakeDecoded();
        }

        public void EndDrain()
        {
            foreach (var stream in _streams)
                stream.EndDrain();
        }

        public void Flush()
        {
            foreach (var stream in _streams)
                stream.Flush();
            Array.Clear(_decodedCount);
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
                stream?.Dispose();
        }
    }
}
