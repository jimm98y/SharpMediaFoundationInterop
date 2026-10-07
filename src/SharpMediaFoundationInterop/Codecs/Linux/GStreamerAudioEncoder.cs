using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using SharpMediaFoundationInterop.Utils;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of GStreamer's, Linux's, of any <see cref="AudioCodec"/>, made for the <see cref="AudioEncoderOptions"/>: an
    /// appsrc of PCM, the first encoder of the codec installed, and its packets out of an appsink. It takes what Media
    /// Foundation's encoder of the codec takes - 16 bit PCM of AAC and MP3, 16 or 24 bit of FLAC and ALAC, 32 bit float of
    /// Opus, interleaved, of more than two channels in WAVE's order - of any length, and hands out a packet at a time.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class GStreamerAudioEncoder : IMediaAudioEncoder
    {
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
        private static readonly int[] AacRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

        private readonly AudioCodec _codec;
        private readonly AudioEncoderOptions _options;
        private GstAppPipeline _pipeline;
        private bool _draining;
        private bool _disposed;

        private byte[] _config;
        // of FLAC: its metadata blocks as the encoder hands them out, and what completes STREAMINFO once drained
        private readonly List<byte[]> _flacBlocks = new List<byte[]>();
        private IncrementalHash _md5;
        private long _samples;
        private int _minFrame = int.MaxValue, _maxFrame;

        public GStreamerAudioEncoder(AudioCodec codec, AudioEncoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            _codec = codec;
            _options = options;
            BitsPerSample = codec == AudioCodec.Opus ? 32 : codec is AudioCodec.Flac or AudioCodec.Alac ? options.BitsPerSample : 16;
            if (BitsPerSample != 16 && BitsPerSample != 24 && BitsPerSample != 32)
                throw new NotSupportedException($"No {codec} of {BitsPerSample} bits");
            Element = Encoders(codec) is { } names ? Gst.FirstElement(names) : null;
        }

        public AudioCodec Codec => _codec;

        /// <summary>The GStreamer element that encodes: of the encoders of the codec, the first installed; null of none.</summary>
        public string Element { get; }

        public uint Channels => _options.Channels;
        public uint SampleRate => _options.SampleRate;
        public uint BitsPerSample { get; }
        public uint OutputSize => 8192 * Math.Max(Channels, 2);
        public Guid InputFormat => _codec == AudioCodec.Opus ? MediaFormats.Float : MediaFormats.PCM;
        public Guid OutputFormat => MediaFormats.Of(_codec);

        private static string[] Encoders(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC => ["fdkaacenc", "avenc_aac", "voaacenc", "faac"],
            AudioCodec.Mp3 => ["lamemp3enc"],
            AudioCodec.Opus => ["opusenc"],
            AudioCodec.Flac => ["flacenc"],
            AudioCodec.Alac => ["avenc_alac"],
            _ => null
        };

        /// <summary>Whether there is an encoder of the codec here: of the plugins installed.</summary>
        public static bool Supports(AudioCodec codec) => Encoders(codec) is { } names && Gst.FirstElement(names) != null;

        /// <summary>
        /// The codec's configuration, for the container to carry: of AAC the AudioSpecificConfig, known once initialized; of
        /// FLAC the metadata blocks, each with its header - what the 'dfLa' box holds - known once the first frame is read
        /// out and complete, of the total samples and MD5, once drained; of ALAC the ALACSpecificConfig; of Opus and MP3 null.
        /// </summary>
        public byte[] Config => _codec switch
        {
            AudioCodec.Flac => FlacConfig(),
            AudioCodec.AAC or AudioCodec.Alac => _config,
            _ => null
        };

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline != null)
                return;
            if (Element == null)
                throw new NotSupportedException($"No GStreamer encoder of {_codec} is installed: of {string.Join(", ", Encoders(_codec) ?? [])}");

            string format = BitsPerSample == 32 ? "F32LE" : BitsPerSample == 24 ? "S24LE" : "S16LE";
            string mask = ChannelMask(Channels) is { } m ? $",channel-mask=(bitmask)0x{m:x}" : "";
            string encoder = _codec switch
            {
                AudioCodec.AAC => $"{Element} ! aacparse ! audio/mpeg,mpegversion=4,stream-format=raw",
                AudioCodec.Mp3 => $"lamemp3enc target=bitrate cbr=true bitrate={Math.Max(_options.Bitrate / 1000, 8)} ! mpegaudioparse",
                AudioCodec.Opus => "opusenc frame-size=20",
                AudioCodec.Flac => "flacenc blocksize=4096",
                _ => Element
            };
            _pipeline = new GstAppPipeline(
                $"appsrc name=src format=time is-live=false block=true max-bytes=4000000 caps=\"audio/x-raw,format={format},layout=interleaved,rate={SampleRate},channels={Channels}{mask}\" ! " +
                $"audioconvert ! {encoder} ! appsink name=sink sync=false");
            _pipeline.Play();

            _config = _codec switch
            {
                AudioCodec.AAC => AudioSpecificConfig(),
                AudioCodec.Alac => AlacSpecificConfig(),
                _ => null
            };
            if (_codec == AudioCodec.Flac)
                _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }

        /// <summary>
        /// The channel mask of WAVE's orders, as GStreamer's positions: front left and right, center, LFE, rear left and
        /// right, rear center, side left and right. Null of mono and stereo, which need none.
        /// </summary>
        private static ulong? ChannelMask(uint channels) => channels switch
        {
            3 => 0x7,
            4 => 0x33,
            5 => 0x37,
            6 => 0x3F,
            7 => 0xD0F,
            8 => 0xC3F,
            _ => null
        };

        /// <summary>AAC-LC's AudioSpecificConfig of the rate and channels: object type 2, the rate's index, the channel configuration.</summary>
        private byte[] AudioSpecificConfig()
        {
            int index = Array.IndexOf(AacRates, (int)SampleRate);
            uint channels = Channels == 8 ? 7 : Channels; // 7.1 is configuration 7
            if (index < 0)
                return null;
            int value = (2 << 11) | (index << 7) | ((int)channels << 3);
            return [(byte)(value >> 8), (byte)value];
        }

        /// <summary>The ALACSpecificConfig of Apple's encoder's defaults, as of Windows' encoder: frames of 4096, pb 40, mb 10, kb 14.</summary>
        private byte[] AlacSpecificConfig()
        {
            var config = new byte[24];
            config[2] = 0x10; // frameLength 4096
            config[5] = (byte)BitsPerSample;
            config[6] = 40;
            config[7] = 10;
            config[8] = 14;
            config[9] = (byte)Channels;
            config[10] = 0;
            config[11] = 255; // maxRun
            config[20] = (byte)(SampleRate >> 24);
            config[21] = (byte)(SampleRate >> 16);
            config[22] = (byte)(SampleRate >> 8);
            config[23] = (byte)SampleRate;
            return config;
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>PCM in, of any length; the packets it makes are read with ProcessOutput.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.IsEmpty)
                return false;
            if (_md5 != null)
            {
                // FLAC's MD5 is of the samples as they are: signed, little-endian, interleaved - the PCM in
                _md5.AppendData(data);
                _samples += data.Length / (Channels * BitsPerSample / 8);
            }
            return _pipeline.Push(ReadOnlySpan<byte>.Empty, data, null);
        }

        /// <summary>The next packet. While draining it waits for one, up to the stream's end.</summary>
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
                byte[] packet;
                try
                {
                    packet = GstFrames.Bytes(sample);
                    TakeConfig(Gst.gst_sample_get_caps(sample));
                }
                finally
                {
                    Gst.gst_mini_object_unref(sample);
                }

                if (IsHeader(packet))
                    continue;
                if (_codec == AudioCodec.Flac)
                {
                    _minFrame = Math.Min(_minFrame, packet.Length);
                    _maxFrame = Math.Max(_maxFrame, packet.Length);
                }
                if (buffer == null || buffer.Length < packet.Length)
                    buffer = new byte[Math.Max(packet.Length, OutputSize)];
                Buffer.BlockCopy(packet, 0, buffer, 0, packet.Length);
                length = (uint)packet.Length;
                return true;
            }
        }

        /// <summary>The configuration the encoder's caps carry, where they carry one: of AAC and ALAC, codec_data.</summary>
        private void TakeConfig(IntPtr caps)
        {
            if (caps == IntPtr.Zero || _codec is not (AudioCodec.AAC or AudioCodec.Alac))
                return;
            byte[] data = Gst.GetCapsBuffer(caps, "codec_data");
            if (data == null)
                return;
            // of ALAC, the 'alac' atom libav makes: the ALACSpecificConfig is its last 24 bytes
            _config = _codec == AudioCodec.Alac && data.Length > 24 ? data.AsSpan(data.Length - 24).ToArray() : data;
        }

        /// <summary>
        /// Whether a packet is of the stream's header, not of its sound: of FLAC the marker and the metadata blocks - kept, as
        /// <see cref="Config"/> - and of Opus its OpusHead and OpusTags.
        /// </summary>
        private bool IsHeader(byte[] packet)
        {
            if (_codec == AudioCodec.Flac && packet.Length >= 4)
            {
                if (packet[0] == 'f' && packet[1] == 'L' && packet[2] == 'a' && packet[3] == 'C')
                    return true;
                if (packet[0] != 0xFF)
                {
                    // a metadata block: one of its type before it is replaced
                    int type = packet[0] & 0x7F;
                    _flacBlocks.RemoveAll(b => (b[0] & 0x7F) == type);
                    _flacBlocks.Add(packet);
                    _flacBlocks.Sort((a, b) => (a[0] & 0x7F) == 0 ? -1 : (b[0] & 0x7F) == 0 ? 1 : 0);
                    return true;
                }
            }
            if (_codec == AudioCodec.Opus && packet.Length >= 8)
            {
                var head = packet.AsSpan(0, 8);
                if (head.SequenceEqual("OpusHead"u8) || head.SequenceEqual("OpusTags"u8))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The metadata blocks, STREAMINFO first, each with its header, the last marked so: of STREAMINFO, once drained, the
        /// frame sizes, the total samples and the MD5 of what went in.
        /// </summary>
        private byte[] FlacConfig()
        {
            if (_flacBlocks.Count == 0)
                return null;
            var blocks = new List<byte>();
            for (int i = 0; i < _flacBlocks.Count; i++)
            {
                var block = (byte[])_flacBlocks[i].Clone();
                block[0] = (byte)((block[0] & 0x7F) | (i == _flacBlocks.Count - 1 ? 0x80 : 0));
                if ((block[0] & 0x7F) == 0 && block.Length >= 4 + 34 && _md5 != null && _samples > 0 && _maxFrame > 0)
                {
                    var info = block.AsSpan(4);
                    info[4] = (byte)(_minFrame >> 16); info[5] = (byte)(_minFrame >> 8); info[6] = (byte)_minFrame;
                    info[7] = (byte)(_maxFrame >> 16); info[8] = (byte)(_maxFrame >> 8); info[9] = (byte)_maxFrame;
                    // the total samples, 36 bits after the rate, channels and bits, then the MD5
                    info[13] = (byte)((info[13] & 0xF0) | (int)((_samples >> 32) & 0x0F));
                    info[14] = (byte)(_samples >> 24); info[15] = (byte)(_samples >> 16); info[16] = (byte)(_samples >> 8); info[17] = (byte)_samples;
                    if (_md5Final != null)
                        _md5Final.CopyTo(info.Slice(18));
                }
                blocks.AddRange(block);
            }
            return blocks.ToArray();
        }

        /// <summary>The MD5 of all that went in: of FLAC, known once the stream is ended, as no more goes in.</summary>
        private byte[] _md5Final;

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Ends the stream, so that everything the encoder holds comes out; read it out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _pipeline.EndOfStream();
            _draining = true;
            if (_md5 != null)
                _md5Final = _md5.GetHashAndReset();
        }

        public void EndDrain()
        {
            ThrowIfNotInitialized();
            _draining = false;
            _pipeline.Flush();
        }

        public void Flush()
        {
            ThrowIfNotInitialized();
            _pipeline.Flush();
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipeline == null)
                throw new InvalidOperationException("The encoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _pipeline?.Dispose();
            _pipeline = null;
            _md5?.Dispose();
        }
    }
}
