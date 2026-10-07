using System;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A decoder of GStreamer's, Linux's, of any <see cref="AudioCodec"/>, made for the <see cref="AudioDecoderOptions"/>: an
    /// appsrc of the codec's caps, its configuration in them, decodebin, and PCM out of an appsink - of the format Media
    /// Foundation's decoder of the codec hands out: 16 bit of AAC and MP3, the stream's 16 or 24 of FLAC and ALAC, 32 bit
    /// float of Opus, interleaved, of more than two channels in the order WAVE has them, which is GStreamer's own.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerAudioDecoder : IMediaAudioTransform
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

        private readonly AudioCodec _codec;
        private readonly string _caps;
        private readonly byte[] _flacHeader;
        private readonly uint _skipSamples;
        private uint _skipBytes;

        private GstAppPipeline _pipeline;
        private bool _draining;
        private bool _disposed;

        public GStreamerAudioDecoder(AudioCodec codec, AudioDecoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            _codec = codec;
            _skipSamples = options.SkipSamples;

            uint channels = options.Channels, rate = options.SampleRate, bits = 16;
            uint maxFrames;
            switch (codec)
            {
                case AudioCodec.AAC:
                    var asc = options.Config ?? throw new ArgumentException("AAC needs its AudioSpecificConfig", nameof(options));
                    _caps = $"audio/mpeg,mpegversion=4,stream-format=raw,codec_data=(buffer){Gst.ToHex(asc)},rate={rate},channels={channels}";
                    maxFrames = 2048 * 2;
                    break;

                case AudioCodec.Mp3:
                    _caps = $"audio/mpeg,mpegversion=1,layer=3,rate={rate},channels={channels}";
                    maxFrames = 1152 * 2;
                    break;

                case AudioCodec.Opus:
                    // Opus is of 48 kHz whatever it was made of: what is asked of the decoder is resampled to
                    _caps = $"audio/x-opus,channel-mapping-family=0,rate=48000,channels={channels}";
                    bits = 32;
                    maxFrames = (uint)(5760L * Math.Max(rate, 48000) / 48000) + 1;
                    break;

                case AudioCodec.Flac:
                    var blocks = options.Config ?? throw new ArgumentException("FLAC needs its metadata blocks", nameof(options));
                    ReadStreamInfo(blocks, out channels, out rate, out bits, out maxFrames);
                    // the stream's header in front of its frames, as a file has it: the parser frames what follows of it
                    _flacHeader = new byte[4 + blocks.Length];
                    "fLaC"u8.CopyTo(_flacHeader);
                    blocks.CopyTo(_flacHeader, 4);
                    MarkLastBlock(_flacHeader.AsSpan(4));
                    _caps = "audio/x-flac";
                    break;

                case AudioCodec.Alac:
                    var config = options.Config ?? throw new ArgumentException("ALAC needs its ALACSpecificConfig", nameof(options));
                    if (config.Length < 24)
                        throw new ArgumentException("An ALACSpecificConfig is of 24 bytes.", nameof(options));
                    var specific = config.AsSpan(config.Length - 24);
                    maxFrames = (uint)((specific[0] << 24) | (specific[1] << 16) | (specific[2] << 8) | specific[3]);
                    bits = specific[5];
                    channels = specific[9];
                    rate = (uint)((specific[20] << 24) | (specific[21] << 16) | (specific[22] << 8) | specific[23]);
                    // libav's decoder takes the 'alac' atom whole: its size, type, version and flags, then the config
                    var atom = new byte[36];
                    atom[3] = 36;
                    "alac"u8.CopyTo(atom.AsSpan(4));
                    specific.CopyTo(atom.AsSpan(12));
                    _caps = $"audio/x-alac,codec_data=(buffer){Gst.ToHex(atom)},rate={rate},channels={channels},samplesize={bits}";
                    break;

                default:
                    throw new NotSupportedException($"No GStreamer decoder of {codec}");
            }

            Channels = channels;
            SampleRate = rate;
            BitsPerSample = bits == 32 ? 32 : bits > 16 ? 24u : 16u;
            OutputSize = maxFrames * Channels * BitsPerSample / 8;
        }

        public AudioCodec Codec => _codec;
        public uint Channels { get; }
        public uint SampleRate { get; }
        public uint BitsPerSample { get; }
        public uint OutputSize { get; }
        public Guid InputFormat => MediaFormats.Of(_codec);
        public Guid OutputFormat => _codec == AudioCodec.Opus ? MediaFormats.Float : MediaFormats.PCM;

        private string OutputCaps =>
            $"audio/x-raw,format={(BitsPerSample == 32 ? "F32LE" : BitsPerSample == 24 ? "S24LE" : "S16LE")},layout=interleaved,rate={SampleRate},channels={Channels}";

        /// <summary>Whether there is a decoder of the codec here: of the plugins installed, one decodebin would pick.</summary>
        public static bool Supports(AudioCodec codec) => Gst.HasDecoderFor(codec switch
        {
            AudioCodec.AAC => "audio/mpeg,mpegversion=4,stream-format=raw",
            AudioCodec.Mp3 => "audio/mpeg,mpegversion=1,layer=3,parsed=true",
            AudioCodec.Opus => "audio/x-opus",
            AudioCodec.Flac => "audio/x-flac,framed=true",
            AudioCodec.Alac => "audio/x-alac",
            _ => "audio/x-unknown"
        });

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"No GStreamer decoder of {_codec} is installed: of gstreamer1.0-libav, -plugins-good, -bad or -ugly");

            _pipeline = new GstAppPipeline(
                $"appsrc name=src format=time is-live=false block=true max-bytes=4000000 caps=\"{_caps}\" ! " +
                $"decodebin ! audioconvert ! audioresample ! {OutputCaps} ! appsink name=sink sync=false");
            _pipeline.Play();
            if (_flacHeader != null)
                _pipeline.Push(ReadOnlySpan<byte>.Empty, _flacHeader, null);
            _skipBytes = SkipBytes;
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One packet in - an AAC access unit, a FLAC frame - of the time given.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            return _pipeline.Push(ReadOnlySpan<byte>.Empty, data, timestamp);
        }

        /// <summary>
        /// The next sound out, past what is to be skipped: that is moved off the front of the buffer, and an output that is
        /// all of it is passed over for the next. The buffer is made larger where it is too small.
        /// </summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            ThrowIfNotInitialized();
            while (true)
            {
                IntPtr sample = _pipeline.Pull(_draining ? DrainTimeout : TimeSpan.Zero);
                if (sample == IntPtr.Zero)
                {
                    length = 0;
                    return false;
                }
                byte[] pcm;
                try
                {
                    pcm = GstFrames.Bytes(sample);
                }
                finally
                {
                    Gst.gst_mini_object_unref(sample);
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

        /// <summary>Ends the stream, so that everything the decoder holds comes out; read it out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _pipeline.EndOfStream();
            _draining = true;
        }

        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _pipeline.Flush();
        }

        /// <summary>Lets go of everything held, for a seek: what is to be skipped is skipped again, as the decoder settles in anew.</summary>
        public void Flush()
        {
            ThrowIfNotInitialized();
            _pipeline.Flush();
            _skipBytes = SkipBytes;
        }

        private uint SkipBytes => _skipSamples * Channels * (BitsPerSample / 8);

        /// <summary>
        /// The stream's format, of its STREAMINFO (RFC 9639 8.2), the first block, after its header: the max block size in its
        /// bytes 2 and 3, then past the frame sizes the rate in 20 bits, the channels less one in 3, the bits less one in 5.
        /// </summary>
        private static void ReadStreamInfo(byte[] blocks, out uint channels, out uint rate, out uint bits, out uint maxBlockSize)
        {
            if (blocks.Length < 4 + 34 || (blocks[0] & 0x7F) != 0)
                throw new ArgumentException("FLAC's metadata blocks begin with STREAMINFO.", nameof(blocks));
            var s = blocks.AsSpan(4);
            maxBlockSize = (uint)((s[2] << 8) | s[3]);
            rate = (uint)((s[10] << 12) | (s[11] << 4) | (s[12] >> 4));
            channels = (uint)(((s[12] >> 1) & 0x7) + 1);
            bits = (uint)((((s[12] & 0x1) << 4) | (s[13] >> 4)) + 1);
        }

        /// <summary>Sets the last-metadata-block flag of the last of the blocks, and only of it: the parser reads frames after it.</summary>
        private static void MarkLastBlock(Span<byte> blocks)
        {
            int i = 0;
            while (i + 4 <= blocks.Length)
            {
                int size = (blocks[i + 1] << 16) | (blocks[i + 2] << 8) | blocks[i + 3];
                bool last = i + 4 + size >= blocks.Length;
                blocks[i] = (byte)((blocks[i] & 0x7F) | (last ? 0x80 : 0));
                i += 4 + size;
            }
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline == null)
                throw new InvalidOperationException("The decoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _pipeline?.Dispose();
            _pipeline = null;
        }
    }
}
