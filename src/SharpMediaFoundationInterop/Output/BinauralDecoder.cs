using System;
using System.IO;
using System.Numerics;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// A sound field heard on headphones: each of its channels - ACN, SN3D, of the first to the third order - convolved with
    /// its HRIR of Google VR's (see Resources/NOTICE.txt) for the left ear; the right ear's is the same of a symmetric head,
    /// but of the harmonics of m below 0, which are the other way round of the right, turned over. Of a field of a higher
    /// order, its third order alone. Of 48 kHz, as the HRIRs are; of another rate, they are resampled to it.
    /// Convolved a block at a time, of the FFT, overlap-add: what comes out is a block late, which the samples a frame in
    /// is a frame out keep it - a block of silence first.
    /// </summary>
    internal sealed class BinauralDecoder
    {
        private const int Block = 256;
        private const int Size = 2 * Block;

        private readonly int _channels;

        /// <summary>Of each channel, its HRIR's spectrum, of <see cref="Size"/>.</summary>
        private readonly Complex[][] _hrirs;

        /// <summary>Of each channel, whether its harmonic is of m below 0: the right ear's, turned over.</summary>
        private readonly bool[] _turned;

        // a block of each channel in, and of head-locked stereo, as it is gathered
        private readonly float[][] _input;
        private readonly float[] _lockedLeft = new float[Block], _lockedRight = new float[Block];
        private int _gathered;

        // the ears' sound: of the last block, and what of it is yet to be read; the tail of the block before it, to add
        private readonly float[] _left = new float[Block], _right = new float[Block];
        private int _read;
        private readonly double[] _tailLeft = new double[Block], _tailRight = new double[Block];

        private readonly Complex[] _spectrum = new Complex[Size];
        private readonly Complex[] _same = new Complex[Size], _opposite = new Complex[Size];

        public BinauralDecoder(int order, uint sampleRate)
        {
            Order = Math.Clamp(order, 1, 3);
            _channels = (Order + 1) * (Order + 1);
            var hrirs = ReadHrirs(Order, sampleRate);

            _hrirs = new Complex[_channels][];
            _turned = new bool[_channels];
            _input = new float[_channels][];
            for (int k = 0; k < _channels; k++)
            {
                var spectrum = new Complex[Size];
                for (int i = 0; i < hrirs[k].Length && i < Block; i++)
                    spectrum[i] = hrirs[k][i];
                Fft(spectrum, false);
                _hrirs[k] = spectrum;
                int l = (int)Math.Sqrt(k);
                _turned[k] = k - l * l - l < 0;
                _input[k] = new float[Block];
            }
            Reset();
        }

        /// <summary>The order of the field it hears: of 1 to 3.</summary>
        public int Order { get; }

        /// <summary>The channels of a frame it takes: of its order.</summary>
        public int Channels => _channels;

        /// <summary>Lets go of what is gathered and heard: a block of silence comes out first again.</summary>
        public void Reset()
        {
            _gathered = 0;
            Array.Clear(_left);
            Array.Clear(_right);
            Array.Clear(_tailLeft);
            Array.Clear(_tailRight);
            _read = 0;
        }

        /// <summary>A frame of the field in - of <see cref="Channels"/> - and of head-locked stereo; the frame a block before it out.</summary>
        public void Process(ReadOnlySpan<float> field, float lockedLeft, float lockedRight, out float left, out float right)
        {
            left = _left[_read];
            right = _right[_read];
            _read++;

            for (int k = 0; k < _channels; k++)
                _input[k][_gathered] = field[k];
            _lockedLeft[_gathered] = lockedLeft;
            _lockedRight[_gathered] = lockedRight;
            if (++_gathered == Block)
            {
                Convolve();
                _gathered = 0;
                _read = 0;
            }
        }

        /// <summary>The block gathered, heard: of each channel, its spectrum by its HRIR's, summed of either ear's sign.</summary>
        private void Convolve()
        {
            Array.Clear(_same);
            Array.Clear(_opposite);
            for (int k = 0; k < _channels; k++)
            {
                var input = _input[k];
                for (int i = 0; i < Block; i++)
                    _spectrum[i] = input[i];
                Array.Clear(_spectrum, Block, Block);
                Fft(_spectrum, false);
                var hrir = _hrirs[k];
                var sum = _turned[k] ? _opposite : _same;
                for (int i = 0; i < Size; i++)
                    sum[i] += _spectrum[i] * hrir[i];
            }

            // left: all of it; right: those of m below 0 turned over
            for (int i = 0; i < Size; i++)
            {
                var same = _same[i];
                _same[i] = same + _opposite[i];
                _opposite[i] = same - _opposite[i];
            }
            Fft(_same, true);
            Fft(_opposite, true);

            for (int i = 0; i < Block; i++)
            {
                _left[i] = (float)(_same[i].Real + _tailLeft[i]) + _lockedLeft[i];
                _right[i] = (float)(_opposite[i].Real + _tailRight[i]) + _lockedRight[i];
                _tailLeft[i] = _same[i + Block].Real;
                _tailRight[i] = _opposite[i + Block].Real;
            }
        }

        /// <summary>An FFT of radix 2, in place; the inverse scaled by 1 / N.</summary>
        private static void Fft(Complex[] data, bool inverse)
        {
            int n = data.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                    (data[i], data[j]) = (data[j], data[i]);
            }
            for (int length = 2; length <= n; length <<= 1)
            {
                double angle = 2 * Math.PI / length * (inverse ? 1 : -1);
                var step = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (int i = 0; i < n; i += length)
                {
                    var w = Complex.One;
                    for (int j = 0; j < length / 2; j++)
                    {
                        var even = data[i + j];
                        var odd = data[i + j + length / 2] * w;
                        data[i + j] = even + odd;
                        data[i + j + length / 2] = even - odd;
                        w *= step;
                    }
                }
            }
            if (inverse)
                for (int i = 0; i < n; i++)
                    data[i] /= n;
        }

        /// <summary>
        /// The HRIRs of the order, of the library's resources: 16 bit PCM of 48 kHz, a channel of each harmonic. Of another
        /// rate, resampled to it, by a windowed sinc, and scaled by the ratio of the rates, to be as loud.
        /// </summary>
        private static float[][] ReadHrirs(int order, uint sampleRate)
        {
            string name = $"SharpMediaFoundationInterop.sh_hrir_order_{order}.wav";
            using var stream = typeof(BinauralDecoder).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The HRIRs {name} are not in the library");
            using var reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != "RIFF")
                throw new InvalidDataException(name);
            reader.ReadUInt32();
            reader.ReadChars(4); // WAVE
            int channels = 0, rate = 0, bits = 0;
            while (true)
            {
                string chunk = new string(reader.ReadChars(4));
                int size = reader.ReadInt32();
                if (chunk == "fmt ")
                {
                    reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    rate = reader.ReadInt32();
                    reader.ReadBytes(6);
                    bits = reader.ReadUInt16();
                    reader.ReadBytes(size - 16);
                }
                else if (chunk == "data")
                {
                    if (bits != 16)
                        throw new InvalidDataException($"{name}: of {bits} bits");
                    int frames = size / (channels * 2);
                    var hrirs = new float[channels][];
                    for (int k = 0; k < channels; k++)
                        hrirs[k] = new float[frames];
                    for (int f = 0; f < frames; f++)
                        for (int k = 0; k < channels; k++)
                            hrirs[k][f] = reader.ReadInt16() / 32768f;
                    return rate == sampleRate ? hrirs : Resample(hrirs, rate, (int)sampleRate);
                }
                else
                {
                    reader.ReadBytes(size + (size & 1));
                }
            }
        }

        private static float[][] Resample(float[][] hrirs, int from, int to)
        {
            double ratio = (double)to / from;
            double cutoff = Math.Min(1, ratio); // below either Nyquist
            const int Taps = 16;
            var resampled = new float[hrirs.Length][];
            for (int k = 0; k < hrirs.Length; k++)
            {
                var source = hrirs[k];
                int length = Math.Min(Block, (int)Math.Ceiling(source.Length * ratio));
                var target = new float[length];
                for (int i = 0; i < length; i++)
                {
                    double t = i / ratio;
                    double sum = 0;
                    for (int j = (int)Math.Floor(t) - Taps + 1; j <= (int)Math.Floor(t) + Taps; j++)
                    {
                        if (j < 0 || j >= source.Length)
                            continue;
                        double x = (t - j) * cutoff;
                        double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                        double window = 0.5 + 0.5 * Math.Cos(Math.PI * (t - j) / Taps);
                        sum += source[j] * sinc * window * cutoff;
                    }
                    // the same sound through filters of fewer, or more, taps a second
                    target[i] = (float)(sum * from / to);
                }
                resampled[k] = target;
            }
            return resampled;
        }
    }
}
