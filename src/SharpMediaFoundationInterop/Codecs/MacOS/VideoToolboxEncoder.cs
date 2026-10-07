using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// An encoder of VideoToolbox's, macOS's, of H.264 or H.265, made for the <see cref="VideoEncoderOptions"/>: NV12 in, of
    /// <see cref="Width"/> by <see cref="Height"/>, and Annex B out, an access unit at a time, start codes and all, as Media
    /// Foundation's encoders hand it out. Each key frame comes with the parameter sets before it - of H.265 the VPS, SPS and
    /// PPS, of H.264 the SPS and PPS - so that what is written of the stream needs nothing besides. It encodes no B-frames:
    /// the access units come out in the order the frames went in, each with the time of its frame.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    public sealed unsafe class VideoToolboxEncoder : IMediaVideoEncoder
    {
        private static readonly byte[] StartCode = [0, 0, 0, 1];

        private readonly VideoCodec _codec;
        private readonly VideoEncoderOptions _options;
        private readonly List<string> _unapplied = new List<string>();
        private readonly Queue<(byte[] Data, long Time)> _encoded = new Queue<(byte[], long)>();
        private readonly object _encodedLock = new object();

        private IntPtr _session;
        private GCHandle _self;
        private bool _disposed;

        public VideoToolboxEncoder(VideoCodec codec, VideoEncoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (codec != VideoCodec.H264 && codec != VideoCodec.H265)
                throw new NotSupportedException($"No VideoToolbox encoder of {codec}: macOS encodes H.264 and H.265");
            if (options.Width == 0 || options.Height == 0)
                throw new ArgumentException("The picture's size is to be given", nameof(options));

            _codec = codec;
            _options = options;
        }

        /// <summary>The codec asked for.</summary>
        public VideoCodec Codec => _codec;

        public uint OriginalWidth => _options.Width;
        public uint OriginalHeight => _options.Height;
        /// <summary>The picture's size, as given: the NV12 frames in are of it, of rows of no padding.</summary>
        public uint Width => _options.Width;
        public uint Height => _options.Height;

        /// <summary>A buffer that holds any access unit: as large as a frame of NV12. One larger still is made larger as it is read.</summary>
        public uint OutputSize => Width * Height * 3 / 2;

        public Guid InputFormat => MediaFormats.NV12;
        public Guid OutputFormat => MediaFormats.Of(_codec);

        public IReadOnlyList<string> UnappliedSettings => _unapplied;

        /// <summary>
        /// Whether there is an encoder of the codec here: of H.264 on every Mac; of H.265 where VideoToolbox makes a session
        /// of it - on Apple silicon, and Intel Macs of a GPU that encodes it.
        /// </summary>
        public static bool Supports(VideoCodec codec)
        {
            if (codec == VideoCodec.H264)
                return true;
            if (codec != VideoCodec.H265)
                return false;
            lock (HevcSupport)
            {
                if (HevcSupport[0] == null)
                {
                    int status = VTCompressionSessionCreate(IntPtr.Zero, 64, 64, CodecType(VideoCodec.H265), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        &OnEncoded, IntPtr.Zero, out IntPtr session);
                    HevcSupport[0] = status == 0;
                    if (status == 0)
                    {
                        VTCompressionSessionInvalidate(session);
                        CFRelease(session);
                    }
                }
                return HevcSupport[0].Value;
            }
        }

        private static readonly bool?[] HevcSupport = new bool?[1];

        private static uint CodecType(VideoCodec codec) => FourCC(codec == VideoCodec.H264 ? "avc1" : "hvc1");

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session != IntPtr.Zero)
                return;

            IntPtr attributes = CreateDictionary();
            SetNumber(attributes, kCVPixelBufferPixelFormatTypeKey, (int)kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange);
            SetNumber(attributes, kCVPixelBufferWidthKey, (int)Width);
            SetNumber(attributes, kCVPixelBufferHeightKey, (int)Height);

            // weak: VideoToolbox's callback must not keep alive an encoder no one disposed
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
            int status = VTCompressionSessionCreate(IntPtr.Zero, (int)Width, (int)Height, CodecType(_codec), IntPtr.Zero, attributes, IntPtr.Zero,
                &OnEncoded, GCHandle.ToIntPtr(_self), out _session);
            CFRelease(attributes);
            if (status != 0)
            {
                _session = IntPtr.Zero;
                _self.Free();
                throw new NotSupportedException($"No VideoToolbox encoder of {_codec} of {Width}x{Height}: {FourCCString(status)}");
            }

            ApplySettings();
            VTCompressionSessionPrepareToEncodeFrames(_session);
        }

        /// <summary>
        /// The options, as VideoToolbox's properties: what it does not take is reported in <see cref="UnappliedSettings"/>,
        /// as of Media Foundation's encoders.
        /// </summary>
        private void ApplySettings()
        {
            _unapplied.Clear();
            var o = _options;

            // what Media Foundation's encoders do of themselves: the stream in order, a profile any decoder takes
            Set("kVTCompressionPropertyKey_AllowFrameReordering", Boolean(false), "B-frames off");
            Set("kVTCompressionPropertyKey_ProfileLevel",
                VideoToolboxConstant(_codec == VideoCodec.H264 ? "kVTProfileLevel_H264_High_AutoLevel" : "kVTProfileLevel_HEVC_Main_AutoLevel"), "profile");
            if (o.FpsNom > 0 && o.FpsDenom > 0)
                SetNumber("kVTCompressionPropertyKey_ExpectedFrameRate", (double)o.FpsNom / o.FpsDenom, nameof(o.FpsNom));

            switch (o.RateControl)
            {
                case RateControlMode.Default:
                case RateControlMode.VariableBitrate:
                    // Media Foundation's encoders are given the bit rate whatever the mode: an average
                    SetNumber("kVTCompressionPropertyKey_AverageBitRate", (int)o.Bitrate, nameof(o.Bitrate));
                    break;

                case RateControlMode.ConstantBitrate:
                    SetNumber("kVTCompressionPropertyKey_ConstantBitRate", (int)o.Bitrate, nameof(o.RateControl));
                    break;

                case RateControlMode.Quality:
                    SetNumber("kVTCompressionPropertyKey_Quality", Math.Clamp(o.Quality, 0u, 100u) / 100.0, nameof(o.Quality));
                    break;

                case RateControlMode.ConstantQp:
                    // the quantiser held between the least and the most allowed, of every frame alike
                    SetNumber("kVTCompressionPropertyKey_MinAllowedFrameQP", (int)o.Qp, nameof(o.Qp));
                    SetNumber("kVTCompressionPropertyKey_MaxAllowedFrameQP", (int)o.Qp, nameof(o.Qp));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(o.RateControl), o.RateControl, "unknown rate control mode");
            }

            if (o.KeyFrameInterval > 0)
                SetNumber("kVTCompressionPropertyKey_MaxKeyFrameInterval", (int)o.KeyFrameInterval, nameof(o.KeyFrameInterval));
            if (o.Threads > 0)
                _unapplied.Add($"{nameof(o.Threads)}: not supported by the encoder");
        }

        private void Set(string key, IntPtr value, string setting)
        {
            IntPtr name = VideoToolboxConstant(key);
            int status = name == IntPtr.Zero ? -1 : VTSessionSetProperty(_session, name, value);
            if (status != 0)
                _unapplied.Add($"{setting}: {(name == IntPtr.Zero ? "not supported" : "rejected")} by the encoder");
        }

        private void SetNumber(string key, int value, string setting)
        {
            IntPtr number = CreateNumber(value);
            Set(key, number, setting);
            CFRelease(number);
        }

        private void SetNumber(string key, double value, string setting)
        {
            IntPtr number = CreateNumber(value);
            Set(key, number, setting);
            CFRelease(number);
        }

        private static void SetNumber(IntPtr dictionary, IntPtr key, int value)
        {
            IntPtr number = CreateNumber(value);
            SetValue(dictionary, key, number);
            CFRelease(number);
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>One NV12 frame in, of <see cref="Width"/> by <see cref="Height"/>, and its time; read what it makes with ProcessOutput.</summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (data.Length < Width * Height * 3 / 2)
                throw new ArgumentException($"An NV12 frame of {Width}x{Height} is of {Width * Height * 3 / 2} bytes", nameof(data));

            IntPtr pool = VTCompressionSessionGetPixelBufferPool(_session);
            if (pool == IntPtr.Zero || CVPixelBufferPoolCreatePixelBuffer(IntPtr.Zero, pool, out IntPtr pixelBuffer) != 0)
                throw new InvalidOperationException("VideoToolbox has no buffer for the frame");
            try
            {
                PixelBuffers.FillNV12(pixelBuffer, data, Width, Height);
                int status = VTCompressionSessionEncodeFrame(_session, pixelBuffer, CMTime.FromTicks(timestamp), CMTime.Invalid, IntPtr.Zero, IntPtr.Zero, out _);
                if (status != 0)
                {
                    if (Log.WarnEnabled)
                        Log.Warn($"VideoToolbox could not encode a {_codec} frame: {FourCCString(status)}");
                    return false;
                }
                return true;
            }
            finally
            {
                CFRelease(pixelBuffer);
            }
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>The next access unit, Annex B, and the time of its frame. The buffer is made larger where it is too small.</summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            ThrowIfNotInitialized();
            (byte[] Data, long Time) unit;
            lock (_encodedLock)
            {
                if (!_encoded.TryDequeue(out unit))
                {
                    length = 0;
                    timestamp = 0;
                    return false;
                }
            }
            if (buffer == null || buffer.Length < unit.Data.Length)
                buffer = new byte[Math.Max(unit.Data.Length, OutputSize)];
            Buffer.BlockCopy(unit.Data, 0, buffer, 0, unit.Data.Length);
            length = (uint)unit.Data.Length;
            timestamp = unit.Time;
            return true;
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Encodes every frame VideoToolbox holds; read them out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            VTCompressionSessionCompleteFrames(_session, CMTime.Invalid);
        }

        public void EndDrain()
        {
        }

        /// <summary>Lets go of what is encoded and not read: the frames VideoToolbox holds are finished first, then dropped.</summary>
        public void Flush()
        {
            ThrowIfNotInitialized();
            VTCompressionSessionCompleteFrames(_session, CMTime.Invalid);
            lock (_encodedLock)
                _encoded.Clear();
        }

        /// <summary>An access unit of VideoToolbox's: its NAL units, each of its length first, made Annex B - after the parameter sets, of a key frame.</summary>
        [UnmanagedCallersOnly]
        private static void OnEncoded(IntPtr refCon, IntPtr sourceFrameRefCon, int status, uint infoFlags, IntPtr sampleBuffer)
        {
            try
            {
                if (refCon == IntPtr.Zero || GCHandle.FromIntPtr(refCon).Target is not VideoToolboxEncoder encoder)
                    return;
                if (status != 0 || sampleBuffer == IntPtr.Zero)
                {
                    if (status != 0 && Log.WarnEnabled)
                        Log.Warn($"VideoToolbox encoded no {encoder._codec} frame: {FourCCString(status)}");
                    return;
                }

                IntPtr data = CMSampleBufferGetDataBuffer(sampleBuffer);
                if (data == IntPtr.Zero)
                    return;
                int size = (int)CMBlockBufferGetDataLength(data);
                var avcc = new byte[size];
                fixed (byte* p = avcc)
                    CMBlockBufferCopyDataBytes(data, 0, (nuint)size, p);

                var unit = new List<byte>(size + 256);
                if (IsKeyFrame(sampleBuffer))
                    encoder.AppendParameterSets(CMSampleBufferGetFormatDescription(sampleBuffer), unit);
                for (int i = 0; i + 4 <= size;)
                {
                    int length = (avcc[i] << 24) | (avcc[i + 1] << 16) | (avcc[i + 2] << 8) | avcc[i + 3];
                    i += 4;
                    if (length <= 0 || i + length > size)
                        break;
                    unit.AddRange(StartCode);
                    unit.AddRange(new ArraySegment<byte>(avcc, i, length));
                    i += length;
                }

                long time = CMSampleBufferGetPresentationTimeStamp(sampleBuffer).ToTicks();
                lock (encoder._encodedLock)
                    encoder._encoded.Enqueue((unit.ToArray(), time));
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled)
                    Log.Error($"An encoded frame could not be taken: {ex}");
            }
        }

        /// <summary>Whether the sample is a sync sample: one without kCMSampleAttachmentKey_NotSync.</summary>
        private static bool IsKeyFrame(IntPtr sampleBuffer)
        {
            IntPtr attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
            if (attachments == IntPtr.Zero || CFArrayGetCount(attachments) == 0)
                return true;
            IntPtr notSync = CFDictionaryGetValue(CFArrayGetValueAtIndex(attachments, 0), kCMSampleAttachmentKey_NotSync);
            return notSync == IntPtr.Zero || notSync == Boolean(false);
        }

        private void AppendParameterSets(IntPtr format, List<byte> unit)
        {
            if (format == IntPtr.Zero)
                return;
            nuint count = 1;
            for (nuint i = 0; i < count; i++)
            {
                byte* set;
                nuint size;
                int status = _codec == VideoCodec.H264
                    ? CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, i, out set, out size, out count, out _)
                    : CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(format, i, out set, out size, out count, out _);
                if (status != 0)
                    return;
                unit.AddRange(StartCode);
                unit.AddRange(new ReadOnlySpan<byte>(set, (int)size).ToArray());
            }
        }

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session == IntPtr.Zero)
                throw new InvalidOperationException("The encoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_session != IntPtr.Zero)
            {
                VTCompressionSessionInvalidate(_session);
                CFRelease(_session);
                _session = IntPtr.Zero;
            }
            if (_self.IsAllocated)
                _self.Free();
            GC.SuppressFinalize(this);
        }

        ~VideoToolboxEncoder()
        {
            if (_session != IntPtr.Zero)
            {
                VTCompressionSessionInvalidate(_session);
                CFRelease(_session);
            }
        }
    }
}
