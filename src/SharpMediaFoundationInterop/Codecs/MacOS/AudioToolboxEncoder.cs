using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of AudioToolbox's, macOS's, of AAC, FLAC, ALAC or Opus, made for the <see cref="AudioEncoderOptions"/>: an
    /// AudioConverter of PCM to the codec. It takes what Media Foundation's encoder of the codec takes - 16 bit PCM of AAC,
    /// 16 or 24 bit of FLAC and ALAC, 32 bit float of Opus, interleaved, of more than two channels in WAVE's order - of any
    /// length, and hands out a packet at a time: of AAC 1024 samples, of FLAC and ALAC 4096, of Opus 20 ms. macOS has no MP3
    /// encoder.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    public sealed unsafe class AudioToolboxEncoder : IMediaAudioEncoder
    {
        private static readonly int NoMoreDataNow = (int)FourCC("nmdn");
        private static readonly uint kAudioConverterInputChannelLayout = FourCC("icl ");

        private readonly AudioCodec _codec;
        private readonly AudioStreamBasicDescription _input;
        private AudioStreamBasicDescription _output;

        private IntPtr _converter;
        private bool _disposed;

        /// <summary>The PCM taken and not yet handed to the converter: of _pcmStart to _pcmEnd.</summary>
        private byte[] _pcm = new byte[65536];
        private int _pcmStart;
        private int _pcmEnd;

        private readonly Queue<byte[]> _packets = new Queue<byte[]>();
        private byte[] _packetBuffer;

        [StructLayout(LayoutKind.Sequential)]
        private struct InputState
        {
            public byte* Data;
            public uint Frames;
            public uint FrameBytes;
            public uint Channels;
            /// <summary>0, PCM to hand the converter; 1, it is taken; 2, the stream's end.</summary>
            public int State;
        }

        public AudioToolboxEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            _codec = codec;

            uint channels = options.Channels, rate = options.SampleRate;
            uint bits = codec == AudioCodec.Opus ? 32 : codec is AudioCodec.Flac or AudioCodec.Alac ? options.BitsPerSample : 16;
            if (codec is AudioCodec.Flac or AudioCodec.Alac && bits != 16 && bits != 24)
                throw new NotSupportedException($"No {codec} of {bits} bits: of 16 or 24");

            uint frameBytes = channels * bits / 8;
            _input = new AudioStreamBasicDescription
            {
                FormatID = kAudioFormatLinearPCM,
                FormatFlags = (codec == AudioCodec.Opus ? kAudioFormatFlagIsFloat : kAudioFormatFlagIsSignedInteger) | kAudioFormatFlagIsPacked,
                SampleRate = rate,
                ChannelsPerFrame = channels,
                BitsPerChannel = bits,
                FramesPerPacket = 1,
                BytesPerFrame = frameBytes,
                BytesPerPacket = frameBytes
            };

            _output = codec switch
            {
                AudioCodec.AAC => new AudioStreamBasicDescription { FormatID = FourCC("aac "), FramesPerPacket = 1024 },
                // of the bits of the PCM they are made of: kAppleLosslessFormatFlag_16BitSourceData, 1, or _24BitSourceData, 3
                AudioCodec.Flac => new AudioStreamBasicDescription { FormatID = FourCC("flac"), FramesPerPacket = 4096, FormatFlags = bits == 24 ? 3u : 1u },
                AudioCodec.Alac => new AudioStreamBasicDescription { FormatID = FourCC("alac"), FramesPerPacket = 4096, FormatFlags = bits == 24 ? 3u : 1u },
                AudioCodec.Opus => new AudioStreamBasicDescription { FormatID = FourCC("opus"), FramesPerPacket = rate / 50 },
                AudioCodec.Mp3 => throw new NotSupportedException("macOS has no MP3 encoder"),
                _ => throw new NotSupportedException($"No AudioToolbox encoder of {codec}")
            };
            _output.SampleRate = rate;
            _output.ChannelsPerFrame = channels;
        }

        /// <summary>The codec asked for.</summary>
        public AudioCodec Codec => _codec;

        public uint Channels => _input.ChannelsPerFrame;
        public uint SampleRate => (uint)_input.SampleRate;
        public uint BitsPerSample => _input.BitsPerChannel;

        public Guid InputFormat => _codec == AudioCodec.Opus ? MediaFormats.Float : MediaFormats.PCM;
        public Guid OutputFormat => MediaFormats.Of(_codec);

        /// <summary>The largest packet the encoder makes, known once it is initialized.</summary>
        public uint OutputSize { get; private set; }

        /// <summary>Whether there is an encoder of the codec here: of AAC, FLAC, ALAC and Opus, of no MP3.</summary>
        public static bool Supports(AudioCodec codec)
        {
            uint format = codec switch
            {
                AudioCodec.AAC => FourCC("aac "),
                AudioCodec.Opus => FourCC("opus"),
                AudioCodec.Flac => FourCC("flac"),
                AudioCodec.Alac => FourCC("alac"),
                _ => 0
            };
            return format != 0 && AudioFormatGetPropertyInfo(kAudioFormatProperty_Encoders, 4, &format, out uint size) == 0 && size > 0;
        }

        /// <summary>
        /// The codec's configuration, for the container to carry, known once the encoder is initialized: of AAC the
        /// AudioSpecificConfig; of FLAC the metadata blocks, each with its header - what the 'dfLa' box holds - complete, of
        /// the total samples and MD5, once the encoder is drained; of ALAC the ALACSpecificConfig; of Opus null, as of Windows.
        /// </summary>
        public byte[] Config
        {
            get
            {
                if (_converter == IntPtr.Zero || _codec == AudioCodec.Opus)
                    return null;
                byte[] cookie = ReadCookie();
                if (cookie == null)
                    return null;
                return _codec switch
                {
                    AudioCodec.AAC => SampleDescriptions.ReadDecoderSpecificInfo(cookie),
                    AudioCodec.Flac => cookie.Length > 12 ? cookie.AsSpan(12).ToArray() : null, // the blocks, of the 'dfLa' box
                    AudioCodec.Alac => AlacSpecificConfig(cookie),
                    _ => null
                };
            }
        }

        private byte[] ReadCookie()
        {
            if (AudioConverterGetPropertyInfo(_converter, kAudioConverterCompressionMagicCookie, out uint size, out _) != 0 || size == 0)
                return null;
            var cookie = new byte[size];
            fixed (byte* p = cookie)
            {
                if (AudioConverterGetProperty(_converter, kAudioConverterCompressionMagicCookie, &size, p) != 0)
                    return null;
            }
            return size == cookie.Length ? cookie : cookie.AsSpan(0, (int)size).ToArray();
        }

        /// <summary>
        /// The ALACSpecificConfig of the magic cookie: the cookie itself, of 24 bytes; or, of one in atoms - 'frma', then
        /// 'alac' - what the 'alac' atom holds past its size, type, version and flags.
        /// </summary>
        private static byte[] AlacSpecificConfig(byte[] cookie)
        {
            if (cookie.Length == 24)
                return cookie;
            for (int i = 0; i + 12 + 24 <= cookie.Length; i++)
            {
                if (cookie[i + 4] == 'a' && cookie[i + 5] == 'l' && cookie[i + 6] == 'a' && cookie[i + 7] == 'c')
                {
                    int size = (cookie[i] << 24) | (cookie[i + 1] << 16) | (cookie[i + 2] << 8) | cookie[i + 3];
                    if (size >= 12 + 24 && i + size <= cookie.Length)
                        return cookie.AsSpan(i + 12, 24).ToArray();
                }
            }
            return cookie.Length > 24 ? cookie.AsSpan(cookie.Length - 24).ToArray() : cookie;
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_converter != IntPtr.Zero)
                return;

            // the rest of the output's description, as the codec has it
            var output = _output;
            uint size = (uint)sizeof(AudioStreamBasicDescription);
            if (AudioFormatGetProperty(kAudioFormatProperty_FormatInfo, 0, null, &size, &output) == 0)
                _output = output;

            var source = _input;
            var destination = _output;
            int status = AudioConverterNew(&source, &destination, out _converter);
            if (status != 0)
            {
                _converter = IntPtr.Zero;
                throw new NotSupportedException($"No AudioToolbox encoder of {_codec} of {SampleRate} Hz, {Channels} channels, {BitsPerSample} bits: {FourCCString(status)}");
            }

            uint tag = Channels switch
            {
                // WAVE's orders, of CoreAudioBaseTypes.h: what Windows' encoders take
                3 => 0x00710003u,
                4 => 0x00B90004u,
                5 => 0x00BA0005u,
                6 => 0x00BB0006u,
                7 => 0x00BC0007u,
                8 => 0x00BD0008u,
                _ => 0u
            };
            if (tag != 0)
            {
                var layout = new AudioChannelLayout { Tag = tag };
                status = AudioConverterSetProperty(_converter, kAudioConverterInputChannelLayout, (uint)sizeof(AudioChannelLayout), &layout);
                if (status != 0 && Log.WarnEnabled)
                    Log.Warn($"The {_codec} encoder takes its own order of channels, not WAVE's: {FourCCString(status)}");
            }

            uint maxPacket = 0;
            size = sizeof(uint);
            AudioConverterGetProperty(_converter, kAudioConverterPropertyMaximumOutputPacketSize, &size, &maxPacket);
            OutputSize = Math.Max(maxPacket, 1024 * Channels);
            _packetBuffer = new byte[OutputSize];
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>PCM in, of any length: as many packets as it completes are made now, read with ProcessOutput; the rest waits for more.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            Append(data);
            Encode(endOfStream: false);
            return true;
        }

        /// <summary>The next packet out. The buffer is made larger where it is too small.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            ThrowIfNotInitialized();
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

        /// <summary>Encodes all it holds, as at the stream's end - the last packet padded; read it out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            Encode(endOfStream: true);
        }

        /// <summary>Takes input again after a drain: the converter, ended, starts anew.</summary>
        public void EndDrain()
        {
            ThrowIfNotInitialized();
            AudioConverterReset(_converter);
        }

        public void Flush()
        {
            ThrowIfNotInitialized();
            AudioConverterReset(_converter);
            _packets.Clear();
            _pcmStart = _pcmEnd = 0;
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            if (_pcmEnd + data.Length > _pcm.Length)
            {
                int held = _pcmEnd - _pcmStart;
                if (held + data.Length > _pcm.Length)
                    Array.Resize(ref _pcm, Math.Max(_pcm.Length * 2, held + data.Length));
                Buffer.BlockCopy(_pcm, _pcmStart, _pcm, 0, held);
                _pcmStart = 0;
                _pcmEnd = held;
            }
            data.CopyTo(_pcm.AsSpan(_pcmEnd));
            _pcmEnd += data.Length;
        }

        /// <summary>
        /// Hands the converter all the whole frames held, and takes every packet it makes of them, one at a time. What it
        /// takes and does not yet make a packet of, it keeps.
        /// </summary>
        private void Encode(bool endOfStream)
        {
            uint frameBytes = _input.BytesPerFrame;
            fixed (byte* pcm = _pcm)
            fixed (byte* output = _packetBuffer)
            {
                var state = new InputState
                {
                    Data = pcm + _pcmStart,
                    Frames = (uint)(_pcmEnd - _pcmStart) / frameBytes,
                    FrameBytes = frameBytes,
                    Channels = Channels,
                    State = 0
                };
                while (true)
                {
                    uint packets = 1;
                    var description = new AudioStreamPacketDescription();
                    var list = new AudioBufferList
                    {
                        NumberBuffers = 1,
                        Buffer = new AudioBuffer { NumberChannels = _output.ChannelsPerFrame, DataByteSize = (uint)_packetBuffer.Length, Data = output }
                    };
                    int status = AudioConverterFillComplexBuffer(_converter, &ProvideInput, (IntPtr)(&state), &packets, &list, &description);
                    if (packets > 0)
                    {
                        uint size = description.DataByteSize != 0 ? description.DataByteSize : list.Buffer.DataByteSize;
                        _packets.Enqueue(_packetBuffer.AsSpan(0, (int)size).ToArray());
                        continue;
                    }
                    if (status == NoMoreDataNow && endOfStream)
                    {
                        state.State = 2; // all is in: the end, so the converter hands out what it holds
                        continue;
                    }
                    if (status != 0 && status != NoMoreDataNow && Log.WarnEnabled)
                        Log.Warn($"AudioToolbox could not encode {_codec}: {FourCCString(status)}");
                    break;
                }

                // what was handed over is the converter's now; a part of a frame stays for the next
                if (state.State != 0)
                    _pcmStart += (int)(state.Frames * frameBytes);
                if (_pcmStart == _pcmEnd)
                    _pcmStart = _pcmEnd = 0;
            }
        }

        /// <summary>The converter's input: the frames held, then nothing more for now - or, at the stream's end, nothing at all.</summary>
        [UnmanagedCallersOnly]
        private static int ProvideInput(IntPtr converter, uint* packets, AudioBufferList* data, AudioStreamPacketDescription** descriptions, IntPtr userData)
        {
            var state = (InputState*)userData;
            if (state->State != 0 || state->Frames == 0)
            {
                if (state->State == 0)
                    state->State = 1;
                *packets = 0;
                data->Buffer.DataByteSize = 0;
                data->Buffer.Data = null;
                return state->State == 2 ? 0 : NoMoreDataNow;
            }

            data->NumberBuffers = 1;
            data->Buffer.NumberChannels = state->Channels;
            data->Buffer.DataByteSize = state->Frames * state->FrameBytes;
            data->Buffer.Data = state->Data;
            *packets = state->Frames;
            state->State = 1;
            return 0;
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_converter == IntPtr.Zero)
                throw new InvalidOperationException("The encoder is to be initialized first.");
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

        ~AudioToolboxEncoder()
        {
            if (_converter != IntPtr.Zero)
                AudioConverterDispose(_converter);
        }
    }
}
