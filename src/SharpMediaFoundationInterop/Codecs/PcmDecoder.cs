using System;
using System.Buffers.Binary;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// Uncompressed PCM, of any <see cref="PcmFormat"/> - 8, 16, 24 or 32 bit integers, 32 or 64 bit floats, of either
    /// byte order, 8 bit of offset binary or signed - made into what is played: little-endian signed integers of 16 bits,
    /// of a stream of 16 or fewer, else of 32. Of no platform's: a frame in is a frame out, at once, nothing held.
    /// </summary>
    public sealed class PcmDecoder : IMediaAudioTransform
    {
        private readonly PcmFormat _format;
        private readonly int _inputBytes;
        private readonly int _outputBytes;

        // what ProcessInput made of its input, handed out by ProcessOutput
        private byte[] _output = Array.Empty<byte>();
        private int _outputLength;

        public PcmDecoder(AudioDecoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            _format = options.Pcm ?? new PcmFormat();
            bool supported = _format.Companding != PcmCompanding.None ? _format.BitsPerSample == 8 : _format.IsFloat
                ? _format.BitsPerSample is 32 or 64
                : _format.BitsPerSample is 8 or 16 or 24 or 32;
            if (!supported)
                throw new NotSupportedException($"PCM of {_format.BitsPerSample} bit {(_format.IsFloat ? "floats" : "integers")}");

            Channels = options.Channels;
            SampleRate = options.SampleRate;
            _inputBytes = (int)_format.BitsPerSample / 8;
            _outputBytes = !_format.IsFloat && _format.BitsPerSample <= 16 ? 2 : 4;
            BitsPerSample = (uint)_outputBytes * 8;
        }

        /// <summary>
        /// A-law's byte expanded (ITU-T G.711 Table 1a): its even bits inverted, then its segment - bits 6 to 4 - and its
        /// step - bits 3 to 0 - of a 13 bit value, scaled to 16 bits; its sign bit 7, of 1 positive.
        /// </summary>
        public static short ExpandALaw(byte a)
        {
            a ^= 0x55;
            int value = (a & 0x0F) << 4;
            int segment = (a & 0x70) >> 4;
            value += segment == 0 ? 8 : 0x108;
            if (segment > 1)
                value <<= segment - 1;
            return (short)((a & 0x80) != 0 ? value : -value);
        }

        /// <summary>
        /// Mu-law's byte expanded (ITU-T G.711 Table 2a): its bits inverted, then its segment and step of a 14 bit value
        /// biased by 33, scaled to 16 bits; its sign bit 7, of 1 negative.
        /// </summary>
        public static short ExpandMuLaw(byte u)
        {
            u = (byte)~u;
            int value = (((u & 0x0F) << 3) + 0x84) << ((u & 0x70) >> 4);
            return (short)((u & 0x80) != 0 ? 0x84 - value : value - 0x84);
        }

        public uint Channels { get; }
        public uint SampleRate { get; }

        /// <summary>The bits of a sample handed out: 16 of a stream of 16 or fewer bits, else 32.</summary>
        public uint BitsPerSample { get; }

        public Guid InputFormat => MediaFormats.PCM;
        public Guid OutputFormat => MediaFormats.PCM;

        /// <summary>A tenth of a second of what is handed out: more is handed out in a buffer grown for it.</summary>
        public uint OutputSize => Math.Max(1, SampleRate / 10) * Channels * (uint)_outputBytes;

        public void Initialize() { }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput((ReadOnlySpan<byte>)data, timestamp);

        /// <summary>Frames in, converted at once: a partial sample at the end, of no frame, is dropped.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            int samples = data.Length / _inputBytes;
            if (samples == 0)
                return false;

            int needed = _outputLength + samples * _outputBytes;
            if (_output.Length < needed)
                Array.Resize(ref _output, Math.Max(needed, _output.Length * 2));
            var output = _output.AsSpan(_outputLength, samples * _outputBytes);
            bool little = _format.IsLittleEndian;

            for (int i = 0; i < samples; i++)
            {
                var sample = data.Slice(i * _inputBytes, _inputBytes);
                if (_format.Companding != PcmCompanding.None)
                {
                    short expanded = _format.Companding == PcmCompanding.ALaw ? ExpandALaw(sample[0]) : ExpandMuLaw(sample[0]);
                    BinaryPrimitives.WriteInt16LittleEndian(output.Slice(i * 2), expanded);
                    continue;
                }
                if (_outputBytes == 2)
                {
                    short value = _format.BitsPerSample == 8
                        ? (short)((_format.IsSigned ? (sbyte)sample[0] : sample[0] - 128) << 8)
                        : little ? BinaryPrimitives.ReadInt16LittleEndian(sample) : BinaryPrimitives.ReadInt16BigEndian(sample);
                    BinaryPrimitives.WriteInt16LittleEndian(output.Slice(i * 2), value);
                    continue;
                }

                int wide;
                if (_format.IsFloat)
                {
                    double real = _format.BitsPerSample == 32
                        ? (little ? BinaryPrimitives.ReadSingleLittleEndian(sample) : BinaryPrimitives.ReadSingleBigEndian(sample))
                        : (little ? BinaryPrimitives.ReadDoubleLittleEndian(sample) : BinaryPrimitives.ReadDoubleBigEndian(sample));
                    // of -1 to 1, as Opus's floats are made integers
                    wide = (int)(Math.Clamp(real, -1.0, 1.0) * int.MaxValue);
                }
                else if (_format.BitsPerSample == 24)
                {
                    // the 24 bits in the high ones of 32
                    wide = little
                        ? sample[0] << 8 | sample[1] << 16 | sample[2] << 24
                        : sample[2] << 8 | sample[1] << 16 | sample[0] << 24;
                }
                else
                {
                    wide = little ? BinaryPrimitives.ReadInt32LittleEndian(sample) : BinaryPrimitives.ReadInt32BigEndian(sample);
                }
                BinaryPrimitives.WriteInt32LittleEndian(output.Slice(i * 4), wide);
            }
            _outputLength = needed;
            return true;
        }

        /// <summary>What the input made, at once: into the buffer, grown where it is too small for it.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            length = 0;
            if (_outputLength == 0)
                return false;
            if (buffer == null || buffer.Length < _outputLength)
                buffer = new byte[_outputLength];
            Buffer.BlockCopy(_output, 0, buffer, 0, _outputLength);
            length = (uint)_outputLength;
            _outputLength = 0;
            return true;
        }

        // nothing is held: a drain has nothing more to hand out, and a flush lets go of what is not yet handed out
        public bool Drain() => true;
        public void BeginDrain() { }
        public void EndDrain() { }
        public void Flush() => _outputLength = 0;

        public void Dispose() { }
    }
}
