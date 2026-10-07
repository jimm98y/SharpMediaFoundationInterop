using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A decoder of VideoToolbox's, macOS's, of any <see cref="VideoCodec"/>, made for the <see cref="VideoDecoderOptions"/>.
    /// It takes what a decoder of Media Foundation's takes - NAL units, OBUs or pictures, each with its time - and hands NV12
    /// out, in the order the frames are shown, each padded to <see cref="Width"/> by <see cref="Height"/>.
    /// <para>
    /// VideoToolbox decodes an access unit at a time, of a configuration it is told before: so the NAL units of H.264 and
    /// H.265, and the OBUs of AV1, are gathered into access units - one is complete as the next begins, or as it is drained -
    /// and the decoder is made as the configuration it needs comes in band: of the parameter sets, of the headers before
    /// the first MPEG-4 picture, of the first VP9 key frame, of the AV1 sequence header. What comes before that is dropped.
    /// </para>
    /// <para>
    /// VideoToolbox hands frames out in the order they are decoded, so they are put in order of their times here. A frame is
    /// held until as many have followed it as can be shown before it - of H.264 and H.265 what the SPS says - so frames of
    /// one time, all of 0 say, come out as they were decoded.
    /// </para>
    /// <para>
    /// Of MPEG-4 Part 2, VideoToolbox decodes the simple profile alone: a stream of the advanced simple profile - of B-VOPs, as
    /// XviD and DivX often are - throws <see cref="NotSupportedException"/> as its headers come in.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    public sealed unsafe class VideoToolboxDecoder : IMediaVideoTransform
    {
        /// <summary>
        /// The multiple the frames' width and height are rounded up to: 2, for NV12's chroma, whatever the codec - the
        /// frames VideoToolbox hands out are of the picture's size, which is copied into the top left of each.
        /// </summary>
        public const uint ResMultiple = 2;

        private readonly VideoCodec _codec;
        private readonly bool _lowLatency;

        private readonly object _outputLock = new object();
        /// <summary>The decoded frames, retained, in order of their times; of frames of one time, as they were decoded.</summary>
        private readonly List<(long Time, IntPtr PixelBuffer)> _frames = new List<(long, IntPtr)>();
        /// <summary>The frames decoded whose time has not yet come out: of the access units gone in, the order they go in.</summary>
        private int _reorderDepth;
        private bool _draining;

        private GCHandle _self;
        private IntPtr _session;
        private IntPtr _format;
        private bool _initialized;
        private bool _disposed;
        private bool _warnedOfSize;
        private bool _warnedOfFormat;

        /// <summary>Of H.264, H.265 and AV1, what gathers their NAL units or OBUs into access units, AVCC's of the NAL units.</summary>
        private readonly AccessUnitAssembler _units;
        /// <summary>The time of the sample being decoded.</summary>
        private long _accessUnitTime;
        /// <summary>Of VP9, AV1 and MPEG-2, the configuration the decoder was made of: made anew where it changes.</summary>
        private byte[] _configAtom;

        public VideoToolboxDecoder(VideoCodec codec, VideoDecoderOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (!Supports(codec))
                throw new NotSupportedException($"No VideoToolbox decoder of {codec}");

            _codec = codec;
            _lowLatency = options.LowLatency;
            OriginalWidth = options.Width;
            OriginalHeight = options.Height;
            Width = MediaUtils.RoundToMultipleOf(Math.Max(options.Width, 2), ResMultiple);
            Height = MediaUtils.RoundToMultipleOf(Math.Max(options.Height, 2), ResMultiple);
            OutputSize = Width * Height * 3 / 2;

            if (codec is VideoCodec.H264 or VideoCodec.H265 or VideoCodec.AV1)
            {
                _units = new AccessUnitAssembler(codec, lengthPrefixed: true);
                _units.AccessUnit += OnAccessUnit;
                _units.SequenceHeader += (av1C, width, height) => UpdateConfig(av1C, width, height);
            }
        }

        /// <summary>The codec asked for.</summary>
        public VideoCodec Codec => _codec;

        public uint OriginalWidth { get; }
        public uint OriginalHeight { get; }
        public uint Width { get; }
        public uint Height { get; }
        public uint OutputSize { get; }

        /// <summary>The Media Foundation subtype of the codec, as Media Foundation's decoder of it has: what is reported alike on every system.</summary>
        public Guid InputFormat => MediaFormats.Of(_codec);
        public Guid OutputFormat => MediaFormats.NV12;

        /// <summary>
        /// Whether there is a decoder of the codec here. Of VP9 and AV1, Apple's are of the GPU alone, so not of every Mac:
        /// of AV1, of the M3 and later.
        /// </summary>
        public static bool Supports(VideoCodec codec)
        {
            switch (codec)
            {
                case VideoCodec.Mpeg4:
                case VideoCodec.H264:
                case VideoCodec.H265:
                    return true;
                case VideoCodec.H262:
                case VideoCodec.H263:
                case VideoCodec.ProRes:
                    // a decoder of them is made of a format of the size alone, where there is one: of MPEG-2 and H.263 of
                    // every Mac and no iPhone; of ProRes of every Mac, and the iPhones and iPads of ProRes
                    return CanCreateSession(CodecType(codec, false));
                case VideoCodec.VP9:
                case VideoCodec.AV1:
                    uint type = CodecType(codec, false);
                    // macOS registers its VP9 and AV1 decoders on request; iOS has its own, where it has them, of itself
                    if (OperatingSystem.IsMacOSVersionAtLeast(11))
                        VTRegisterSupplementalVideoDecoderIfAvailable(type);
                    return VTIsHardwareDecodeSupported(type) != 0;
                default:
                    return false;
            }
        }

        private static readonly Dictionary<uint, bool> Sessions = new Dictionary<uint, bool>();

        /// <summary>Whether VideoToolbox makes a decompression session of the codec, of a format of the size alone.</summary>
        private static bool CanCreateSession(uint codecType)
        {
            lock (Sessions)
            {
                if (!Sessions.TryGetValue(codecType, out bool can))
                {
                    IntPtr session = IntPtr.Zero;
                    can = CMVideoFormatDescriptionCreate(IntPtr.Zero, codecType, 640, 480, IntPtr.Zero, out IntPtr format) == 0 &&
                        VTDecompressionSessionCreate(IntPtr.Zero, format, IntPtr.Zero, IntPtr.Zero, null, out session) == 0;
                    if (can)
                    {
                        VTDecompressionSessionInvalidate(session);
                        CFRelease(session);
                    }
                    if (format != IntPtr.Zero)
                        CFRelease(format);
                    Sessions[codecType] = can;
                }
                return can;
            }
        }

        private static uint CodecType(VideoCodec codec, bool mpeg1) => codec switch
        {
            VideoCodec.H262 => FourCC(mpeg1 ? "mp1v" : "mp2v"),
            VideoCodec.H263 => FourCC("h263"),
            VideoCodec.Mpeg4 => FourCC("mp4v"),
            VideoCodec.H264 => FourCC("avc1"),
            VideoCodec.H265 => FourCC("hvc1"),
            VideoCodec.VP9 => FourCC("vp09"),
            VideoCodec.AV1 => FourCC("av01"),
            VideoCodec.ProRes => FourCC("apch"),
            _ => throw new NotSupportedException($"No VideoToolbox decoder of {codec}")
        };

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;
            if (!Supports(_codec))
                throw new NotSupportedException($"No VideoToolbox decoder of {_codec} on this Mac");

            // weak: VideoToolbox's callback must not keep alive a decoder no one disposed, whose finalizer lets go of the session
            _self = GCHandle.Alloc(this, GCHandleType.Weak);
            _initialized = true;

            // H.263 needs nothing of the stream: its decoder is made now
            if (_codec == VideoCodec.H263)
                CreateSession(CreateFormat(FourCC("h263"), (int)OriginalWidth, (int)OriginalHeight, null));
        }

        public bool ProcessInput(byte[] data, long timestamp) => ProcessInput(new ReadOnlySpan<byte>(data), timestamp);

        /// <summary>
        /// One sample in: a NAL unit, with its start code or not, or an access unit of them in Annex B; an OBU, or a temporal
        /// unit of them; a picture. Always taken: what VideoToolbox makes of it is read with ProcessOutput.
        /// </summary>
        public bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            ThrowIfNotInitialized();
            if (_units != null)
                _units.Push(data, timestamp);
            else
                ProcessPicture(data, timestamp);
            return true;
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length) => ProcessOutput(ref buffer, out length, out _);

        /// <summary>
        /// The next frame out, in the order the frames are shown, and its time: the time of the input it came of. The
        /// buffer is made larger where it is smaller than <see cref="OutputSize"/>.
        /// </summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            ThrowIfNotInitialized();
            length = 0;
            timestamp = 0;

            IntPtr pixelBuffer;
            lock (_outputLock)
            {
                if (_frames.Count == 0 || (!_draining && _frames.Count <= _reorderDepth))
                    return false;
                (timestamp, pixelBuffer) = _frames[0];
                _frames.RemoveAt(0);
            }

            try
            {
                if (buffer == null || buffer.Length < OutputSize)
                    buffer = new byte[OutputSize];
                CopyNV12(pixelBuffer, buffer);
                length = OutputSize;
                return true;
            }
            finally
            {
                CFRelease(pixelBuffer);
            }
        }

        public bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>Decodes the access unit being gathered, and everything VideoToolbox holds; read it all out before <see cref="EndDrain"/>.</summary>
        public void BeginDrain()
        {
            ThrowIfNotInitialized();
            _units?.Flush();
            if (_session != IntPtr.Zero)
            {
                VTDecompressionSessionFinishDelayedFrames(_session);
                VTDecompressionSessionWaitForAsynchronousFrames(_session);
            }
            lock (_outputLock)
                _draining = true;
        }

        public void EndDrain()
        {
            lock (_outputLock)
                _draining = false;
        }

        /// <summary>Drops everything held - the access unit being gathered, and the frames not yet read - for a seek.</summary>
        public void Flush()
        {
            ThrowIfNotInitialized();
            _units?.Clear();
            if (_session != IntPtr.Zero)
            {
                VTDecompressionSessionFinishDelayedFrames(_session);
                VTDecompressionSessionWaitForAsynchronousFrames(_session);
            }
            ReleaseFrames();
        }

        #region Input

        private void ProcessPicture(ReadOnlySpan<byte> data, long timestamp)
        {
            if (data.IsEmpty)
                return;

            switch (_codec)
            {
                case VideoCodec.H262:
                    if (SampleDescriptions.ReadMpegSequenceHeader(data, out int width, out int height, out bool mpeg1) &&
                        (_format == IntPtr.Zero || _configAtom == null || _configAtom[0] != (mpeg1 ? 1 : 2)))
                    {
                        // the codec of the sequence header: MPEG-1's, or MPEG-2's
                        _configAtom = [(byte)(mpeg1 ? 1 : 2)];
                        ReplaceSession(CreateFormat(CodecType(VideoCodec.H262, mpeg1), width, height, null));
                    }
                    break;

                case VideoCodec.Mpeg4:
                    if (_format == IntPtr.Zero)
                    {
                        var headers = SampleDescriptions.ReadMpeg4Headers(data);
                        if (headers != null)
                        {
                            // object type 0x20, visual stream 4: MPEG-4 Part 2
                            var esds = SampleDescriptions.CreateEsds(0x20, 0x04, headers);
                            ReplaceSession(CreateFormat(FourCC("mp4v"), (int)OriginalWidth, (int)OriginalHeight, ("esds", esds)));
                        }
                    }
                    break;

                case VideoCodec.VP9:
                    var vpcC = SampleDescriptions.CreateVpcC(data, out int vp9Width, out int vp9Height);
                    if (vpcC != null)
                        UpdateConfig(vpcC, vp9Width, vp9Height);
                    break;

                case VideoCodec.ProRes:
                    // each picture's 'icpf' header says its size and chroma: of 4:2:2, ProRes 422's family, of 4:4:4, 4444's
                    if (SampleDescriptions.ReadProResHeader(data, out int proResWidth, out int proResHeight, out bool chroma444))
                    {
                        byte[] key = [(byte)(proResWidth >> 8), (byte)proResWidth, (byte)(proResHeight >> 8), (byte)proResHeight, chroma444 ? (byte)1 : (byte)0];
                        if (_format == IntPtr.Zero || _configAtom == null || !key.AsSpan().SequenceEqual(_configAtom))
                        {
                            _configAtom = key;
                            ReplaceSession(CreateFormat(FourCC(chroma444 ? "ap4h" : "apch"), proResWidth, proResHeight, null));
                        }
                    }
                    break;
            }

            if (_session == IntPtr.Zero)
                return; // nothing to decode it with: its configuration is yet to come

            _accessUnitTime = timestamp;
            DecodeFrame(data);
        }

        /// <summary>
        /// Of VP9 and AV1, the configuration atom of a key frame or sequence header: the decoder is made anew where it differs
        /// from the one it was made of.
        /// </summary>
        private void UpdateConfig(byte[] atom, int width, int height)
        {
            if (_configAtom != null && _session != IntPtr.Zero && atom.AsSpan().SequenceEqual(_configAtom))
                return;
            _configAtom = atom;
            string name = _codec == VideoCodec.VP9 ? "vpcC" : "av1C";
            ReplaceSession(CreateFormat(CodecType(_codec, false), width, height, (name, atom)));
        }

        /// <summary>An access unit complete: decoded, of the format of the parameter sets, made anew where they changed.</summary>
        private void OnAccessUnit(ReadOnlySpan<byte> unit, long time)
        {
            if (_codec != VideoCodec.AV1 && _units.ParameterSetsChanged)
                UpdateParameterSets();
            if (_session == IntPtr.Zero)
                return; // nothing to decode it with: its configuration is yet to come
            _accessUnitTime = time;
            DecodeFrame(unit);
        }

        /// <summary>The format description of the parameter sets, all of them: the decoder made anew of it where it cannot take it.</summary>
        private void UpdateParameterSets()
        {
            bool hevc = _codec == VideoCodec.H265;
            if (!_units.HasParameterSets)
                return;
            var sets = _units.TakeParameterSets();

            var handles = new GCHandle[sets.Count];
            var pointers = stackalloc byte*[sets.Count];
            var sizes = stackalloc nuint[sets.Count];
            IntPtr format;
            int status;
            try
            {
                for (int i = 0; i < sets.Count; i++)
                {
                    handles[i] = GCHandle.Alloc(sets[i], GCHandleType.Pinned);
                    pointers[i] = (byte*)handles[i].AddrOfPinnedObject();
                    sizes[i] = (nuint)sets[i].Length;
                }
                status = hevc
                    ? CMVideoFormatDescriptionCreateFromHEVCParameterSets(IntPtr.Zero, (nuint)sets.Count, pointers, sizes, 4, IntPtr.Zero, out format)
                    : CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr.Zero, (nuint)sets.Count, pointers, sizes, 4, out format);
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.IsAllocated)
                        handle.Free();
                }
            }

            if (status != 0)
            {
                if (Log.WarnEnabled)
                    Log.Warn($"The parameter sets make no format description: {FourCCString(status)}");
                return;
            }

            lock (_outputLock)
                _reorderDepth = _units.ReorderDepth;

            ReplaceSession(format);
        }

        #endregion

        #region Session

        private static IntPtr CreateFormat(uint codecType, int width, int height, (string Name, byte[] Data)? atom)
        {
            IntPtr extensions = IntPtr.Zero;
            try
            {
                if (atom != null)
                {
                    extensions = CreateDictionary();
                    IntPtr atoms = CreateDictionary();
                    IntPtr name = CreateString(atom.Value.Name);
                    IntPtr data = CreateData(atom.Value.Data);
                    SetValue(atoms, name, data);
                    SetValue(extensions, kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms, atoms);
                    CFRelease(data);
                    CFRelease(name);
                    CFRelease(atoms);
                }

                int status = CMVideoFormatDescriptionCreate(IntPtr.Zero, codecType, width, height, extensions, out IntPtr format);
                if (status != 0)
                    throw new InvalidOperationException($"No format description of the stream: {FourCCString(status)}");
                return format;
            }
            finally
            {
                if (extensions != IntPtr.Zero)
                    CFRelease(extensions);
            }
        }

        /// <summary>
        /// Decodes with the format given from now on: of the decoder there is, where it can take it, or of one made anew - after
        /// the frames the one there was holds are handed out.
        /// </summary>
        private void ReplaceSession(IntPtr format)
        {
            if (_session != IntPtr.Zero && VTDecompressionSessionCanAcceptFormatDescription(_session, format) != 0)
            {
                if (_format != IntPtr.Zero)
                    CFRelease(_format);
                _format = format;
                return;
            }
            CreateSession(format);
        }

        private void CreateSession(IntPtr format)
        {
            DestroySession();
            _format = format;

            if (_codec is VideoCodec.H262 or VideoCodec.Mpeg4)
            {
                // B-pictures: one anchor picture ahead of them
                lock (_outputLock)
                    _reorderDepth = 1;
            }

            IntPtr attributes = CreateDictionary();
            IntPtr pixelFormat = CreateNumber((int)kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange);
            SetValue(attributes, kCVPixelBufferPixelFormatTypeKey, pixelFormat);
            CFRelease(pixelFormat);

            var callback = new VTDecompressionOutputCallbackRecord
            {
                Callback = &OnFrame,
                RefCon = GCHandle.ToIntPtr(_self)
            };
            int status = VTDecompressionSessionCreate(IntPtr.Zero, _format, IntPtr.Zero, attributes, &callback, out _session);
            CFRelease(attributes);
            if (status != 0)
            {
                _session = IntPtr.Zero;
                if (status is kVTCouldNotFindVideoDecoderErr or kVTVideoDecoderUnsupportedDataFormatErr or kVTVideoDecoderNotAvailableNowErr)
                    throw new NotSupportedException($"No VideoToolbox decoder of this {_codec} stream: {FourCCString(status)}");
                if (_codec == VideoCodec.Mpeg4 && status == codecBadDataErr)
                    throw new NotSupportedException("VideoToolbox decodes MPEG-4 Part 2 of the simple profile alone, not of the advanced simple profile - of B-VOPs, quarter pixels or global motion - this stream is of");
                throw new InvalidOperationException($"VideoToolbox made no {_codec} decoder: {FourCCString(status)}");
            }
        }

        private void DestroySession()
        {
            if (_session != IntPtr.Zero)
            {
                // the frames it holds are handed out before it goes
                VTDecompressionSessionFinishDelayedFrames(_session);
                VTDecompressionSessionWaitForAsynchronousFrames(_session);
                VTDecompressionSessionInvalidate(_session);
                CFRelease(_session);
                _session = IntPtr.Zero;
            }
            if (_format != IntPtr.Zero)
            {
                CFRelease(_format);
                _format = IntPtr.Zero;
            }
        }

        private void DecodeFrame(ReadOnlySpan<byte> sample)
        {
            IntPtr blockBuffer = IntPtr.Zero, sampleBuffer = IntPtr.Zero;
            try
            {
                // of memory of Core Media's, as VideoToolbox may hold the sample past the call
                int status = CMBlockBufferCreateWithMemoryBlock(IntPtr.Zero, null, (nuint)sample.Length, IntPtr.Zero, IntPtr.Zero, 0,
                    (nuint)sample.Length, kCMBlockBufferAssureMemoryNowFlag, out blockBuffer);
                if (status == 0)
                {
                    fixed (byte* p = sample)
                        status = CMBlockBufferReplaceDataBytes(p, blockBuffer, 0, (nuint)sample.Length);
                }
                if (status != 0)
                    throw new InvalidOperationException($"No block buffer of the sample: {FourCCString(status)}");

                var timing = new CMSampleTimingInfo
                {
                    Duration = CMTime.Invalid,
                    PresentationTimeStamp = CMTime.FromTicks(_accessUnitTime),
                    DecodeTimeStamp = CMTime.Invalid
                };
                nuint size = (nuint)sample.Length;
                status = CMSampleBufferCreateReady(IntPtr.Zero, blockBuffer, _format, 1, 1, &timing, 1, &size, out sampleBuffer);
                if (status != 0)
                    throw new InvalidOperationException($"No sample buffer of the sample: {FourCCString(status)}");

                status = VTDecompressionSessionDecodeFrame(_session, sampleBuffer, _lowLatency ? 0 : kVTDecodeFrame_EnableTemporalProcessing, IntPtr.Zero, out _);
                if (status != 0 && Log.WarnEnabled)
                    Log.Warn($"VideoToolbox could not decode a {_codec} sample: {FourCCString(status)}");
            }
            finally
            {
                if (sampleBuffer != IntPtr.Zero)
                    CFRelease(sampleBuffer);
                if (blockBuffer != IntPtr.Zero)
                    CFRelease(blockBuffer);
            }
        }

        /// <summary>A frame of VideoToolbox's, decoded: retained, and put in its place among the others by its time.</summary>
        [UnmanagedCallersOnly]
        private static void OnFrame(IntPtr refCon, IntPtr sourceFrameRefCon, int status, uint infoFlags, IntPtr imageBuffer, CMTime presentationTime, CMTime duration)
        {
            try
            {
                if (GCHandle.FromIntPtr(refCon).Target is not VideoToolboxDecoder decoder)
                    return;
                if (status != 0 || imageBuffer == IntPtr.Zero || (infoFlags & kVTDecodeInfo_FrameDropped) != 0)
                {
                    if (status != 0 && Log.WarnEnabled)
                        Log.Warn($"VideoToolbox decoded no {decoder._codec} frame: {FourCCString(status)}");
                    return;
                }

                long time = presentationTime.ToTicks();
                CFRetain(imageBuffer);
                lock (decoder._outputLock)
                {
                    // after every frame of a time no later than its own
                    int i = decoder._frames.Count;
                    while (i > 0 && decoder._frames[i - 1].Time > time)
                        i--;
                    decoder._frames.Insert(i, (time, imageBuffer));
                }
            }
            catch (Exception ex)
            {
                // nothing may be thrown back into VideoToolbox
                if (Log.ErrorEnabled)
                    Log.Error($"A decoded frame could not be taken: {ex}");
            }
        }

        #endregion

        #region Output

        /// <summary>The frame - NV12, as asked of VideoToolbox - into the top left of the buffer's <see cref="Width"/> by <see cref="Height"/>.</summary>
        private void CopyNV12(IntPtr pixelBuffer, byte[] buffer)
        {
            if (!PixelBuffers.CopyNV12(pixelBuffer, buffer, Width, Height, out int frameWidth, out int frameHeight))
            {
                if (!_warnedOfFormat && Log.WarnEnabled)
                    Log.Warn($"VideoToolbox handed out a frame of {FourCCString((int)CVPixelBufferGetPixelFormatType(pixelBuffer))}, not NV12: it is left out");
                _warnedOfFormat = true;
                return;
            }
            if ((frameWidth > Width || frameHeight > Height) && !_warnedOfSize && Log.WarnEnabled)
            {
                Log.Warn($"The frames are of {frameWidth}x{frameHeight}, larger than the {Width}x{Height} the decoder was made for: they are cut");
                _warnedOfSize = true;
            }
        }

        private void ReleaseFrames()
        {
            lock (_outputLock)
            {
                foreach (var frame in _frames)
                    CFRelease(frame.PixelBuffer);
                _frames.Clear();
            }
        }

        #endregion

        private void ThrowIfNotInitialized()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
                throw new InvalidOperationException("The decoder is to be initialized first.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            DestroySession();
            ReleaseFrames();
            if (_self.IsAllocated)
                _self.Free();
            GC.SuppressFinalize(this);
        }

        ~VideoToolboxDecoder()
        {
            // what the system holds: the session's callback refers to this, so it is gone only once the session is
            if (_session != IntPtr.Zero)
            {
                VTDecompressionSessionInvalidate(_session);
                CFRelease(_session);
            }
            if (_format != IntPtr.Zero)
                CFRelease(_format);
            foreach (var frame in _frames)
                CFRelease(frame.PixelBuffer);
        }
    }
}
