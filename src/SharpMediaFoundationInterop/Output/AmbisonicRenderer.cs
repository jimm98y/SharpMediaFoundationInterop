using System;
using System.Buffers.Binary;

namespace SharpMediaFoundationInterop.Output
{
    /// <summary>
    /// Ambisonics - a sound field, of ACN order and SN3D normalization, as Opus' channel mapping family 2 and Google's
    /// spatial audio carry it (RFC 8486, Spatial Audio RFC) - played on speakers, turned as the listener's head is: each
    /// speaker the field's first order heard by a virtual cardioid pointing where the speaker is, seen from the head. Of
    /// a sound field of a higher order, its first order alone: what five speakers can tell apart. Two channels after the
    /// field are head-locked stereo, played on the front left and right as they are, not turned. Or, of <see cref="Binaural"/>,
    /// heard on headphones: the field turned, then heard through HRIRs - see <see cref="BinauralDecoder"/> - to its third
    /// order. The PCM in and out is of 16 or 32 bit integers; a turn is faded in over a buffer, not stepped.
    /// </summary>
    public sealed class AmbisonicRenderer
    {
        private readonly int _order;
        private readonly bool _headLocked;

        /// <summary>Where each speaker is, as <see cref="SpeakerLayout.Directions"/>; where it is heard from, of stereo.</summary>
        private readonly (double Azimuth, double Elevation)?[] _speakers;
        private readonly int _left, _right;
        private readonly double _gain;

        // of each speaker, W Y Z X's gains: those the last buffer ended on, and those of the head as it is turned now
        private readonly float[] _gains, _target;
        private double _yaw, _pitch;
        private bool _turned = true;

        /// <summary>Of headphones, what hears the field; null of speakers.</summary>
        private readonly BinauralDecoder _binaural;

        // of headphones, of each order the field is heard to, the rotation of its harmonics: those the last buffer ended on,
        // and those of the head as it is turned now
        private double[][,] _rotation, _targetRotation;
        private readonly float[] _harmonics, _rotated;

        /// <param name="channels">The channels in: a sound field's, of (order + 1)², and two more of head-locked stereo.</param>
        /// <param name="speakers">The channels out, of the speakers <see cref="SpeakerLayout.ChannelMask"/> says.</param>
        /// <param name="bitsPerSample">Of the PCM in and out: 16 or 32.</param>
        public AmbisonicRenderer(uint channels, uint speakers, uint bitsPerSample)
            : this(channels, bitsPerSample)
        {
            if (speakers == 0)
                throw new ArgumentException("No speakers", nameof(speakers));
            Channels = speakers;

            uint mask = SpeakerLayout.ChannelMask(speakers);
            _speakers = SpeakerLayout.Directions(mask);
            int directed = 0;
            for (int s = 0; s < _speakers.Length; s++)
                if (_speakers[s] != null) directed++;
            // Stereo heard by cardioids at 30 degrees would be all but mono: they point to either side instead.
            if (mask == 0x3)
                _speakers = new (double, double)?[] { (90, 0), (-90, 0) };
            // A diffuse field's sound comes out of each cardioid a third as loud as the field's own, so of more than three
            // speakers it is made quieter, to be as loud in all; not louder of fewer, which would clip what comes from one side.
            _gain = directed > 3 ? Math.Sqrt(3.0 / directed) : 1;

            _left = (mask & 0x1) != 0 ? 0 : -1;
            _right = (mask & 0x2) != 0 ? (_left + 1) : -1;

            _gains = new float[_speakers.Length * 4];
            _target = new float[_speakers.Length * 4];
            Aim();
            Array.Copy(_target, _gains, _gains.Length);
        }

        private AmbisonicRenderer(uint channels, uint bitsPerSample)
        {
            if (!IsAmbisonic(channels, out _order, out _headLocked))
                throw new ArgumentException($"{channels} channels are not of a sound field", nameof(channels));
            if (bitsPerSample != 16 && bitsPerSample != 32)
                throw new ArgumentException($"PCM of {bitsPerSample} bits", nameof(bitsPerSample));
            InputChannels = channels;
            BitsPerSample = bitsPerSample;
        }

        /// <summary>
        /// Of headphones: two channels out, the left ear's and the right's, of the field heard through HRIRs of a head,
        /// turned as the listener looks.
        /// </summary>
        /// <param name="channels">The channels in: a sound field's, of (order + 1)², and two more of head-locked stereo.</param>
        /// <param name="sampleRate">The rate of the PCM: the HRIRs' is 48 kHz, of another they are resampled.</param>
        /// <param name="bitsPerSample">Of the PCM in and out: 16 or 32.</param>
        public static AmbisonicRenderer Binaural(uint channels, uint sampleRate, uint bitsPerSample) =>
            new AmbisonicRenderer(channels, sampleRate, bitsPerSample, binaural: true);

        private AmbisonicRenderer(uint channels, uint sampleRate, uint bitsPerSample, bool binaural)
            : this(channels, bitsPerSample)
        {
            Channels = 2;
            _binaural = new BinauralDecoder(_order, sampleRate);
            _harmonics = new float[_binaural.Channels];
            _rotated = new float[_binaural.Channels];
            _targetRotation = SphericalHarmonicRotation.Of(SphericalHarmonicRotation.OfHead(0, 0), _binaural.Order);
            _rotation = _targetRotation;
            _turned = false;
        }

        /// <summary>Whether it plays to headphones, of <see cref="Binaural"/>, rather than to speakers.</summary>
        public bool IsBinaural => _binaural != null;

        /// <summary>
        /// Lets go of the sound it holds, of headphones: what a seek needs, as the sound starts again elsewhere. Of
        /// speakers it holds none.
        /// </summary>
        public void Reset() => _binaural?.Reset();

        /// <summary>
        /// Whether so many channels are of a sound field - of (order + 1)² of them, of the first order or higher - with or
        /// without two of head-locked stereo after it (RFC 8486 3.1).
        /// </summary>
        public static bool IsAmbisonic(uint channels, out int order, out bool headLocked)
        {
            order = (int)Math.Sqrt(channels) - 1;
            int field = (order + 1) * (order + 1);
            headLocked = channels - field == 2;
            return order >= 1 && order <= 14 && (channels == field || headLocked);
        }

        public uint InputChannels { get; }

        /// <summary>The speakers' channels out; of headphones, 2.</summary>
        public uint Channels { get; }

        public uint BitsPerSample { get; }

        /// <summary>The bytes out of so many in.</summary>
        public int OutputLength(int inputLength) => inputLength / (int)InputChannels * (int)Channels;

        /// <summary>
        /// Where the listener looks, in degrees: to the right of straight ahead - the sound field's front - and up. Of the
        /// next buffer, faded in over it.
        /// </summary>
        public void Turn(double yaw, double pitch)
        {
            if (yaw == _yaw && pitch == _pitch)
                return;
            _yaw = yaw;
            _pitch = pitch;
            _turned = true;
        }

        /// <summary>
        /// Of each speaker, the gains of W, Y, Z and X: of a cardioid pointing where the speaker is, seen from the head as it
        /// is turned - up by the pitch, then right by the yaw, the field's x ahead, y to the left and z up.
        /// </summary>
        private void Aim()
        {
            double yaw = -_yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
            for (int s = 0; s < _speakers.Length; s++)
            {
                var direction = _speakers[s];
                int g = s * 4;
                if (direction == null)
                {
                    _target[g] = _target[g + 1] = _target[g + 2] = _target[g + 3] = 0; // the LFE: none, as a speaker of no direction
                    continue;
                }
                if (_speakers.Length == 1)
                {
                    _target[g] = 1; // mono: the field's W alone
                    _target[g + 1] = _target[g + 2] = _target[g + 3] = 0;
                    continue;
                }

                double azimuth = direction.Value.Azimuth * Math.PI / 180, elevation = direction.Value.Elevation * Math.PI / 180;
                double x = Math.Cos(elevation) * Math.Cos(azimuth), y = Math.Cos(elevation) * Math.Sin(azimuth), z = Math.Sin(elevation);
                // looking up
                double x1 = x * Math.Cos(pitch) - z * Math.Sin(pitch), z1 = x * Math.Sin(pitch) + z * Math.Cos(pitch);
                // looking right
                double x2 = x1 * Math.Cos(yaw) - y * Math.Sin(yaw), y2 = x1 * Math.Sin(yaw) + y * Math.Cos(yaw);

                _target[g] = (float)(0.5 * _gain);
                _target[g + 1] = (float)(0.5 * _gain * y2);
                _target[g + 2] = (float)(0.5 * _gain * z1);
                _target[g + 3] = (float)(0.5 * _gain * x2);
            }
        }

        /// <summary>The speakers' PCM of the sound field's, into <paramref name="output"/>, of <see cref="OutputLength"/>.</summary>
        /// <returns>The bytes out.</returns>
        public int Render(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (_binaural != null)
                return RenderBinaural(input, output);

            if (_turned)
            {
                _turned = false;
                Aim();
            }

            int inChannels = (int)InputChannels, outChannels = (int)Channels, bytes = (int)BitsPerSample / 8;
            int frames = input.Length / (inChannels * bytes);
            int field = (_order + 1) * (_order + 1);
            float scale = bytes == 2 ? 1f / 32768 : 1f / 2147483648;
            float step = frames > 0 ? 1f / frames : 0;

            for (int f = 0; f < frames; f++)
            {
                var frame = input.Slice(f * inChannels * bytes);
                float w = Read(frame, 0, bytes) * scale, y = Read(frame, 1, bytes) * scale,
                      z = Read(frame, 2, bytes) * scale, x = Read(frame, 3, bytes) * scale;
                float left = 0, right = 0;
                if (_headLocked)
                {
                    left = Read(frame, field, bytes) * scale;
                    right = Read(frame, field + 1, bytes) * scale;
                }

                // of the gains the last buffer ended on, to those of the head as it is now
                float t = (f + 1) * step;
                var to = output.Slice(f * outChannels * bytes);
                for (int s = 0; s < outChannels; s++)
                {
                    float value = 0;
                    if (s < _speakers.Length)
                    {
                        int g = s * 4;
                        value = Lerp(_gains[g], _target[g], t) * w + Lerp(_gains[g + 1], _target[g + 1], t) * y
                              + Lerp(_gains[g + 2], _target[g + 2], t) * z + Lerp(_gains[g + 3], _target[g + 3], t) * x;
                    }
                    if (s == _left) value += left;
                    else if (s == _right) value += right;
                    Write(to, s, bytes, value);
                }
            }
            Array.Copy(_target, _gains, _gains.Length);
            return frames * outChannels * bytes;
        }

        /// <summary>
        /// Of headphones: each frame's harmonics, to the order heard, turned - of the rotation the last buffer ended on to the
        /// head's now - then heard, with the head-locked stereo, by the ears.
        /// </summary>
        private int RenderBinaural(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (_turned)
            {
                _turned = false;
                _targetRotation = SphericalHarmonicRotation.Of(SphericalHarmonicRotation.OfHead(_yaw, _pitch), _binaural.Order);
            }

            int inChannels = (int)InputChannels, bytes = (int)BitsPerSample / 8;
            int frames = input.Length / (inChannels * bytes);
            int field = (_order + 1) * (_order + 1), heard = _binaural.Channels;
            float scale = bytes == 2 ? 1f / 32768 : 1f / 2147483648;
            double step = frames > 0 ? 1.0 / frames : 0;
            bool turning = !ReferenceEquals(_rotation, _targetRotation);

            for (int f = 0; f < frames; f++)
            {
                var frame = input.Slice(f * inChannels * bytes);
                for (int k = 0; k < heard; k++)
                    _harmonics[k] = Read(frame, k, bytes) * scale;
                float lockedLeft = 0, lockedRight = 0;
                if (_headLocked)
                {
                    lockedLeft = Read(frame, field, bytes) * scale;
                    lockedRight = Read(frame, field + 1, bytes) * scale;
                }

                // of each order, its harmonics turned
                double t = (f + 1) * step;
                for (int l = 0; l <= _binaural.Order; l++)
                {
                    var from = _rotation[l];
                    var to = _targetRotation[l];
                    int first = l * l, count = 2 * l + 1;
                    for (int m = 0; m < count; m++)
                    {
                        double sum = 0;
                        for (int n = 0; n < count; n++)
                        {
                            double gain = turning ? from[m, n] + (to[m, n] - from[m, n]) * t : to[m, n];
                            sum += gain * _harmonics[first + n];
                        }
                        _rotated[first + m] = (float)sum;
                    }
                }

                _binaural.Process(_rotated, lockedLeft, lockedRight, out float left, out float right);
                var ears = output.Slice(f * 2 * bytes);
                Write(ears, 0, bytes, left);
                Write(ears, 1, bytes, right);
            }
            _rotation = _targetRotation;
            return frames * 2 * bytes;
        }

        private static float Lerp(float from, float to, float t) => from + (to - from) * t;

        private static float Read(ReadOnlySpan<byte> frame, int channel, int bytes) => bytes == 2
            ? BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(channel * 2))
            : BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(channel * 4));

        private static void Write(Span<byte> frame, int channel, int bytes, float value)
        {
            value = Math.Clamp(value, -1f, 1f);
            if (bytes == 2)
                BinaryPrimitives.WriteInt16LittleEndian(frame.Slice(channel * 2), (short)Math.Clamp(value * 32768, short.MinValue, short.MaxValue));
            else
                BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(channel * 4), (int)Math.Clamp(value * 2147483648.0, int.MinValue, int.MaxValue));
        }
    }
}
