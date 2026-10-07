using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A decoder of AudioToolbox's, macOS's, of any <see cref="AudioCodec"/>, made for the <see cref="AudioDecoderOptions"/>:
    /// an AudioConverter of the codec to PCM, of the format Media Foundation's decoder of it hands out - 16 bit of AAC and
    /// MP3, the stream's 16 or 24 of FLAC and ALAC, 32 bit float of Opus - interleaved, of more than two channels in the
    /// order of WAVE's. Each packet in - an AAC access unit, a FLAC frame - is decoded as it comes.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    public sealed unsafe class AudioToolboxDecoder : IMediaAudioTransform
    {
        /// <summary>What the input callback answers once its packet is taken: nothing more yet, rather than the stream's end.</summary>
        private static readonly int NoMoreDataNow = (int)FourCC("nmdn");

        private static readonly int FormatNotSupported = (int)FourCC("fmt?");

        private readonly AudioCodec _codec;
        private AudioStreamBasicDescription _input;
        private AudioStreamBasicDescription _output;
        private readonly byte[] _cookie;
        private readonly uint _skipSamples;
        private uint _skipBytes;

        private IntPtr _converter;
        private bool _disposed;

        private readonly Queue<(byte[] Data, uint Length)> _decoded = new Queue<(byte[], uint)>();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();

        [StructLayout(LayoutKind.Sequential)]
        private struct InputState
        {
            public byte* Data;
            public uint Size;
            public uint Channels;
            /// <summary>0, a packet to hand the converter; 1, it is taken; 2, the stream's end.</summary>
            public int State;
            public AudioStreamPacketDescription Description;
        }

        public AudioToolboxDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            _codec = codec;
            _skipSamples = options.SkipSamples;

            uint channels = options.Channels;
            uint rate = options.SampleRate;
            uint bits = 16;
            uint maxFrames;
            switch (codec)
            {
                case AudioCodec.AAC:
                    if (options.Config == null)
                        throw new ArgumentException("AAC needs its AudioSpecificConfig", nameof(options));
                    // object type 0x40, audio stream 5: MPEG-4 audio
                    _cookie = SampleDescriptions.CreateEsDescriptor(0x40, 0x05, options.Config);
                    _input = new AudioStreamBasicDescription { FormatID = FourCC("aac "), SampleRate = rate, ChannelsPerFrame = channels, FramesPerPacket = 1024 };
                    maxFrames = 2048 * 2; // HE-AAC's 2048, at twice the rate
                    break;

                case AudioCodec.Mp3:
                    _input = new AudioStreamBasicDescription { FormatID = FourCC(".mp3"), SampleRate = rate, ChannelsPerFrame = channels, FramesPerPacket = rate >= 32000 ? 1152u : 576u };
                    maxFrames = 1152 * 2;
                    break;

                case AudioCodec.Opus:
                    // Opus is of 48 kHz whatever it was made of: the converter makes the rate asked of it
                    _input = new AudioStreamBasicDescription { FormatID = FourCC("opus"), SampleRate = 48000, ChannelsPerFrame = channels, FramesPerPacket = 960 };
                    bits = 32;
                    maxFrames = (uint)(5760L * Math.Max(rate, 48000) / 48000) + 1; // the longest packet, of 120 ms
                    break;

                case AudioCodec.Flac:
                    _cookie = CreateFlacCookie(options.Config ?? throw new ArgumentException("FLAC needs its metadata blocks", nameof(options)),
                        out channels, out rate, out bits, out maxFrames);
                    _input = new AudioStreamBasicDescription { FormatID = FourCC("flac"), SampleRate = rate, ChannelsPerFrame = channels, FramesPerPacket = maxFrames };
                    break;

                case AudioCodec.Alac:
                    var config = options.Config ?? throw new ArgumentException("ALAC needs its ALACSpecificConfig", nameof(options));
                    if (config.Length < 24)
                        throw new ArgumentException("An ALACSpecificConfig is of 24 bytes.", nameof(options));
                    _cookie = config;
                    maxFrames = (uint)((config[0] << 24) | (config[1] << 16) | (config[2] << 8) | config[3]);
                    bits = config[5];
                    channels = config[9];
                    rate = (uint)((config[20] << 24) | (config[21] << 16) | (config[22] << 8) | config[23]);
                    _input = new AudioStreamBasicDescription { FormatID = FourCC("alac"), SampleRate = rate, ChannelsPerFrame = channels, FramesPerPacket = maxFrames };
                    break;

                default:
                    throw new NotSupportedException($"No AudioToolbox decoder of {codec}");
            }

            if (bits != 16 && bits != 24 && bits != 32)
                throw new NotSupportedException($"No {codec} of {bits} bits");

            _output = new AudioStreamBasicDescription
            {
                FormatID = kAudioFormatLinearPCM,
                FormatFlags = (bits == 32 ? kAudioFormatFlagIsFloat : kAudioFormatFlagIsSignedInteger) | kAudioFormatFlagIsPacked,
                SampleRate = rate,
                ChannelsPerFrame = channels,
                BitsPerChannel = bits,
                FramesPerPacket = 1,
                BytesPerFrame = channels * bits / 8,
                BytesPerPacket = channels * bits / 8
            };
            OutputSize = maxFrames * _output.BytesPerFrame;
        }

        /// <summary>The codec asked for.</summary>
        public AudioCodec Codec => _codec;

        public Guid InputFormat => MediaFormats.Of(_codec);
        public Guid OutputFormat => _codec == AudioCodec.Opus ? MediaFormats.Float : MediaFormats.PCM;
        public uint OutputSize { get; }

        public uint Channels => _output.ChannelsPerFrame;
        public uint SampleRate => (uint)_output.SampleRate;
        public uint BitsPerSample => _output.BitsPerChannel;

        /// <summary>Whether there is a decoder of the codec here.</summary>
        public static bool Supports(AudioCodec codec)
        {
            uint format = codec switch
            {
                AudioCodec.AAC => FourCC("aac "),
                AudioCodec.Mp3 => FourCC(".mp3"),
                AudioCodec.Opus => FourCC("opus"),
                AudioCodec.Flac => FourCC("flac"),
                AudioCodec.Alac => FourCC("alac"),
                _ => 0
            };
            return format != 0 && AudioFormatGetPropertyInfo(kAudioFormatProperty_Decoders, 4, &format, out uint size) == 0 && size > 0;
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_converter != IntPtr.Zero)
                return;

            if (_cookie != null)
            {
                // the rest of the input's description, as the codec's configuration gives it: HE-AAC's frames and rate, say
                var input = _input;
                uint size = (uint)sizeof(AudioStreamBasicDescription);
                fixed (byte* cookie = _cookie)
                {
                    if (AudioFormatGetProperty(kAudioFormatProperty_FormatInfo, (uint)_cookie.Length, cookie, &size, &input) == 0)
                        _input = input;
                }
            }

            var source = _input;
            var destination = _output;
            int status = AudioConverterNew(&source, &destination, out _converter);
            if (status != 0)
            {
                _converter = IntPtr.Zero;
                if (status == FormatNotSupported)
                    throw new NotSupportedException($"No AudioToolbox decoder of this {_codec} stream");
                throw new InvalidOperationException($"AudioToolbox made no {_codec} decoder: {FourCCString(status)}");
            }

            if (_cookie != null)
            {
                fixed (byte* cookie = _cookie)
                    status = AudioConverterSetProperty(_converter, kAudioConverterDecompressionMagicCookie, (uint)_cookie.Length, cookie);
                if (status != 0)
                    throw new InvalidOperationException($"The {_codec} decoder takes no configuration of this: {FourCCString(status)}");
            }

            uint tag = WaveLayout(Channels);
            if (tag != 0)
            {
                var layout = new AudioChannelLayout { Tag = tag };
                status = AudioConverterSetProperty(_converter, kAudioConverterOutputChannelLayout, (uint)sizeof(AudioChannelLayout), &layout);
                if (status != 0 && Log.WarnEnabled)
                    Log.Warn($"The {_codec} decoder keeps its own order of channels, not WAVE's: {FourCCString(status)}");
            }

            _skipBytes = SkipBytes;
        }

        /// <summary>
        /// The layout of the channels in the order WAVE has them, of more than two: what Windows' decoders hand out. The tags
        /// of CoreAudioBaseTypes.h.
        /// </summary>
        private static uint WaveLayout(uint channels) => channels switch
        {
            3 => 0x00710003, // WAVE_3_0: L R C
            4 => 0x00B90004, // WAVE_4_0_B: L R Rls Rrs
            5 => 0x00BA0005, // WAVE_5_0_B: L R C Rls Rrs
            6 => 0x00BB0006, // WAVE_5_1_B: L R C LFE Rls Rrs
            7 => 0x00BC0007, // WAVE_6_1: L R C LFE Cs Ls Rs
            8 => 0x00BD0008, // WAVE_7_1: L R C LFE Rls Rrs Ls Rs
            _ => 0
        };

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One packet in, decoded now: what it makes is read with ProcessOutput.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;

            fixed (byte* p = data)
            {
                var state = new InputState { Data = p, Size = (uint)data.Length, Channels = _input.ChannelsPerFrame };
                Convert(&state);
            }
            return true;
        }

        /// <summary>
        /// The next sound out, past what is to be skipped: that is moved off the front of the buffer, and an output that is
        /// all of it is passed over for the next. The buffer is made larger where it is too small.
        /// </summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            ThrowIfNotInitialized();
            while (_decoded.TryDequeue(out var decoded))
            {
                uint skip = Math.Min(_skipBytes, decoded.Length);
                _skipBytes -= skip;
                length = decoded.Length - skip;
                if (length > 0)
                {
                    if (buffer == null || buffer.Length < length)
                        buffer = new byte[Math.Max(length, OutputSize)];
                    Buffer.BlockCopy(decoded.Data, (int)skip, buffer, 0, (int)length);
                }
                _pool.Push(decoded.Data);
                if (length > 0)
                    return true;
            }
            length = 0;
            return false;
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Hands out what the decoder holds, as at the stream's end; read it out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            var state = new InputState { State = 2 };
            Convert(&state);
        }

        /// <summary>Takes input again after a drain: the converter, ended, starts anew.</summary>
        public void EndDrain()
        {
            ThrowIfNotInitialized();
            AudioConverterReset(_converter);
        }

        /// <summary>Lets go of everything held, for a seek: what is to be skipped is skipped again, as the decoder settles in anew.</summary>
        public void Flush()
        {
            ThrowIfNotInitialized();
            AudioConverterReset(_converter);
            while (_decoded.TryDequeue(out var decoded))
                _pool.Push(decoded.Data);
            _skipBytes = SkipBytes;
        }

        private uint SkipBytes => _skipSamples * Channels * (BitsPerSample / 8);

        /// <summary>Decodes all the converter makes of the state's packet - or, at the stream's end, of what it holds.</summary>
        private void Convert(InputState* state)
        {
            uint frameBytes = _output.BytesPerFrame;
            uint capacity = OutputSize / frameBytes;
            while (true)
            {
                byte[] chunk = _pool.Count > 0 ? _pool.Pop() : new byte[OutputSize];
                uint frames = capacity;
                int status;
                fixed (byte* p = chunk)
                {
                    var list = new AudioBufferList
                    {
                        NumberBuffers = 1,
                        Buffer = new AudioBuffer { NumberChannels = _output.ChannelsPerFrame, DataByteSize = capacity * frameBytes, Data = p }
                    };
                    status = AudioConverterFillComplexBuffer(_converter, &ProvideInput, (IntPtr)state, &frames, &list, null);
                    frames = Math.Min(frames, list.Buffer.DataByteSize / frameBytes);
                }

                if (frames > 0)
                    _decoded.Enqueue((chunk, frames * frameBytes));
                else
                    _pool.Push(chunk);

                if (status == NoMoreDataNow || frames == 0)
                    break;
                if (status != 0)
                {
                    if (Log.WarnEnabled)
                        Log.Warn($"AudioToolbox could not decode a {_codec} packet: {FourCCString(status)}");
                    break;
                }
            }
        }

        /// <summary>The converter's input: the one packet, then nothing more for now - or, at the stream's end, nothing at all.</summary>
        [UnmanagedCallersOnly]
        private static int ProvideInput(IntPtr converter, uint* packets, AudioBufferList* data, AudioStreamPacketDescription** descriptions, IntPtr userData)
        {
            var state = (InputState*)userData;
            if (state->State != 0)
            {
                *packets = 0;
                data->Buffer.DataByteSize = 0;
                data->Buffer.Data = null;
                return state->State == 2 ? 0 : NoMoreDataNow;
            }

            data->NumberBuffers = 1;
            data->Buffer.NumberChannels = state->Channels;
            data->Buffer.DataByteSize = state->Size;
            data->Buffer.Data = state->Data;
            *packets = 1;
            if (descriptions != null)
            {
                state->Description = new AudioStreamPacketDescription { StartOffset = 0, VariableFramesInPacket = 0, DataByteSize = state->Size };
                *descriptions = &state->Description;
            }
            state->State = 1;
            return 0;
        }

        /// <summary>
        /// The magic cookie of FLAC AudioToolbox takes - a 'dfLa' box, whole, of the metadata blocks - and the stream's format,
        /// of its STREAMINFO (RFC 9639 8.2), the first block, after its header: the max block size in its bytes 2 and 3, then
        /// past the frame sizes the rate in 20 bits, the channels less one in 3, the bits less one in 5.
        /// </summary>
        private static byte[] CreateFlacCookie(byte[] blocks, out uint channels, out uint rate, out uint bits, out uint maxBlockSize)
        {
            if (blocks.Length < 4 + 34 || (blocks[0] & 0x7F) != 0)
                throw new ArgumentException("FLAC's metadata blocks begin with STREAMINFO.", nameof(blocks));
            var s = blocks.AsSpan(4);
            maxBlockSize = (uint)((s[2] << 8) | s[3]);
            rate = (uint)((s[10] << 12) | (s[11] << 4) | (s[12] >> 4));
            channels = (uint)(((s[12] >> 1) & 0x7) + 1);
            bits = (uint)((((s[12] & 0x1) << 4) | (s[13] >> 4)) + 1);

            int size = 12 + blocks.Length;
            var cookie = new byte[size];
            cookie[0] = (byte)(size >> 24);
            cookie[1] = (byte)(size >> 16);
            cookie[2] = (byte)(size >> 8);
            cookie[3] = (byte)size;
            cookie[4] = (byte)'d';
            cookie[5] = (byte)'f';
            cookie[6] = (byte)'L';
            cookie[7] = (byte)'a';
            blocks.CopyTo(cookie, 12);
            return cookie;
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_converter == IntPtr.Zero)
                throw new InvalidOperationException("The decoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_converter != IntPtr.Zero)
            {
                AudioConverterDispose(_converter);
                _converter = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~AudioToolboxDecoder()
        {
            if (_converter != IntPtr.Zero)
                AudioConverterDispose(_converter);
        }
    }
}
