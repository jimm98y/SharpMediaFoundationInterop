namespace SharpMediaFoundationInterop.Transforms
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
        AV1
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
        /// How many threads the encoder may use; 0 leaves it to the encoder. Each keeps working buffers of its own, so
        /// this trades speed against memory.
        /// </summary>
        public uint Threads { get; set; }
    }
}
