namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>The video formats a codec of <see cref="MediaCodecs"/> can be asked for.</summary>
    public enum VideoCodec
    {
        /// <summary>MPEG-2 video, and MPEG-1 video with it.</summary>
        H262,
        H263,
        /// <summary>MPEG-4 Part 2 video.</summary>
        Mpeg4,
        H264,
        H265,
        VP9,
        AV1,

        /// <summary>
        /// Apple ProRes - 422, 422 HQ, LT, Proxy, 4444 and 4444 XQ - each sample a picture, of its 'icpf' header; of
        /// VideoToolbox on macOS and iOS, and of GStreamer's libav on Linux. Encoded of the
        /// <see cref="VideoEncoderOptions.ProResProfile"/>.
        /// </summary>
        ProRes
    }

    /// <summary>The ProRes an encoder makes: each of its own fixed quality, and of its own data rate, lowest first.</summary>
    public enum ProResProfile
    {
        /// <summary>ProRes 422 Proxy, 'apco': for offline editing.</summary>
        Proxy,

        /// <summary>ProRes 422 LT, 'apcs'.</summary>
        LT,

        /// <summary>ProRes 422, 'apcn'.</summary>
        Standard,

        /// <summary>ProRes 422 HQ, 'apch': what an iPhone records.</summary>
        HQ,

        /// <summary>ProRes 4444, 'ap4h': of 4:4:4 chroma - of NV12 in, its chroma made full.</summary>
        P4444,

        /// <summary>ProRes 4444 XQ, 'ap4x': 4444 of a higher data rate still. Not every encoder has it.</summary>
        P4444XQ
    }

    /// <summary>How an encoder spends its bits.</summary>
    public enum RateControlMode
    {
        /// <summary>Whatever the encoder does when told nothing.</summary>
        Default,

        /// <summary>The <see cref="VideoEncoderOptions.Bitrate"/>, held to every second of the stream.</summary>
        ConstantBitrate,

        /// <summary>The <see cref="VideoEncoderOptions.Bitrate"/> on average, more where the picture needs it.</summary>
        VariableBitrate,

        /// <summary>A quality, <see cref="VideoEncoderOptions.Quality"/>, of 0 to 100, whatever it costs.</summary>
        Quality,

        /// <summary>
        /// One quantiser, <see cref="VideoEncoderOptions.Qp"/>, for every picture, of every type: the key frames then cost
        /// no more for their quality than the pictures between them.
        /// </summary>
        ConstantQp
    }

    /// <summary>
    /// The frames a decoder hands out, of the chroma and the bits of the stream: Windows' decoders of HEVC and AV1 hand out
    /// only the one of each stream - P010 of 10 bits of 4:2:0, Y210 of 4:2:2, Y410 of 4:4:4 - and no NV12 of it. Of more
    /// than 8 bits, each sample's bits are its high ones, of 16 - but Y410's. Rows of <see cref="VideoFrameFormats.Stride"/>
    /// bytes, of the decoder's width.
    /// </summary>
    public enum VideoFrameFormat
    {
        /// <summary>4:2:0 of 8 bits: a plane of luma, then one of Cb and Cr interleaved at half the size each way.</summary>
        NV12,

        /// <summary>4:2:0 of 10 bits: as NV12, of 16 bit samples, little-endian.</summary>
        P010,

        /// <summary>4:2:0 of 12 or 16 bits: as P010.</summary>
        P016,

        /// <summary>4:2:2 of 8 bits, packed: of each two pixels Y0, Cb, Y1 and Cr, a byte each.</summary>
        YUY2,

        /// <summary>4:2:2 of 10 bits: as YUY2, of 16 bit samples.</summary>
        Y210,

        /// <summary>4:2:2 of 12 or 16 bits: as Y210.</summary>
        Y216,

        /// <summary>4:4:4 of 8 bits, packed: of each pixel Cr, Cb, Y and alpha, a byte each.</summary>
        AYUV,

        /// <summary>4:4:4 of 10 bits, packed in 32: Cb in bits 0 to 9, Y in 10 to 19, Cr in 20 to 29, alpha in 30 and 31.</summary>
        Y410,

        /// <summary>4:4:4 of 12 or 16 bits, packed: of each pixel Cb, Y, Cr and alpha, of 16 bits each.</summary>
        Y416,

        /// <summary>Monochrome of 8 bits: a plane of luma alone.</summary>
        L8,

        /// <summary>Monochrome of more than 8 bits: a plane of luma alone, of 16 bit samples.</summary>
        L16,
    }

    /// <summary>The sizes of each <see cref="VideoFrameFormat"/>.</summary>
    public static class VideoFrameFormats
    {
        /// <summary>The bytes of a row: of the luma plane, of a planar format.</summary>
        public static uint Stride(VideoFrameFormat format, uint width) => format switch
        {
            VideoFrameFormat.NV12 or VideoFrameFormat.L8 => width,
            VideoFrameFormat.P010 or VideoFrameFormat.P016 or VideoFrameFormat.YUY2 or VideoFrameFormat.L16 => width * 2,
            VideoFrameFormat.Y210 or VideoFrameFormat.Y216 or VideoFrameFormat.AYUV or VideoFrameFormat.Y410 => width * 4,
            VideoFrameFormat.Y416 => width * 8,
            _ => throw new System.ArgumentOutOfRangeException(nameof(format)),
        };

        /// <summary>The bytes of a frame: of 4:2:0, its chroma plane after its luma.</summary>
        public static uint FrameSize(VideoFrameFormat format, uint width, uint height) => format switch
        {
            VideoFrameFormat.NV12 or VideoFrameFormat.P010 or VideoFrameFormat.P016 => Stride(format, width) * height * 3 / 2,
            _ => Stride(format, width) * height,
        };
    }

    /// <summary>What a decoder of <see cref="MediaCodecs.CreateVideoDecoder"/> is made for.</summary>
    public sealed class VideoDecoderOptions
    {
        /// <summary>The picture's width, as coded: for HEVC of 1080 lines, that is 1088 of them.</summary>
        public uint Width { get; set; }

        /// <summary>The picture's height, as coded.</summary>
        public uint Height { get; set; }

        /// <summary>The frame rate, where the stream says it; 0 over 0 where it does not. A hint: nothing is timed by it.</summary>
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; }

        /// <summary>Hands each frame out as soon as it is decoded, rather than holding some back.</summary>
        public bool LowLatency { get; set; }

        /// <summary>How many threads the decoder may use; 0 leaves it to the decoder.</summary>
        public uint Threads { get; set; }

        /// <summary>
        /// The frames handed out: NV12, as every decoder hands out, or what the stream's chroma and bits need - on Windows of
        /// HEVC's and AV1's decoders alone; another decoder throws <see cref="NotSupportedException"/>.
        /// </summary>
        public VideoFrameFormat OutputFormat { get; set; }
    }

    /// <summary>What an encoder of <see cref="MediaCodecs.CreateVideoEncoder"/> is made for, and how it encodes.</summary>
    public sealed class VideoEncoderOptions
    {
        /// <summary>The picture's width.</summary>
        public uint Width { get; set; }

        /// <summary>The picture's height.</summary>
        public uint Height { get; set; }

        /// <summary>The frame rate.</summary>
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; } = 1;

        /// <summary>Bits a second, of the rate control modes that keep to one.</summary>
        public uint Bitrate { get; set; } = 8000000;

        public RateControlMode RateControl { get; set; }

        /// <summary>0 to 100, of <see cref="RateControlMode.Quality"/>.</summary>
        public uint Quality { get; set; }

        /// <summary>The quantiser, of <see cref="RateControlMode.ConstantQp"/>.</summary>
        public uint Qp { get; set; }

        /// <summary>Pictures from one key frame to the next; 0 leaves it to the encoder.</summary>
        public uint KeyFrameInterval { get; set; }

        /// <summary>
        /// Of <see cref="VideoCodec.ProRes"/>, the profile: what sets its quality and data rate, as ProRes has no rate control
        /// of its own. ProRes 422 HQ unless set.
        /// </summary>
        public ProResProfile ProResProfile { get; set; } = ProResProfile.HQ;

        /// <summary>
        /// How many threads the encoder may use; 0 leaves it to the encoder. Each keeps working buffers of its own, so
        /// this trades speed against memory.
        /// </summary>
        public uint Threads { get; set; }
    }
}
