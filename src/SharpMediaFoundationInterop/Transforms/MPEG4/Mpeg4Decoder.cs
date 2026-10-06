using System.Runtime.Versioning;
using System;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.MPEG4
{
    /// <summary>
    /// MPEG-4 Part 2 video - Visual, of ISO/IEC 14496-2: the simple and advanced simple profiles, XviD and DivX - decoded by
    /// Windows' MPEG-4 Part 2 decoder. Its visual object sequence and object layer headers come in band, before the first
    /// picture.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class Mpeg4Decoder : VideoTransformBase
    {
        /// <summary>Its pictures are coded in macroblocks of 16 by 16.</summary>
        public const uint MPEG4_RES_MULTIPLE = 16;


        public override Guid InputFormat => PInvoke.MFVideoFormat_MP4V;
        public override Guid OutputFormat => PInvoke.MFVideoFormat_NV12;

        private bool _isLowLatency = false;

        /// <summary>
        /// Whether B-VOPs are left out: Windows' decoder gives them out blank - of the advanced simple profile, it decodes the
        /// rest - and no other picture refers to one, so the stream's I and P pictures are shown, of fewer a second, rather
        /// than blank pictures between them. On, unless a decoder of B-VOPs is the one made.
        /// </summary>
        public bool SkipBVops { get; set; } = true;

        public Mpeg4Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false)
          : base(MPEG4_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            _isLowLatency = isLowLatency;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE, input, output);
            if (transform == null) transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_DECODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            // on the GPU, where a device was given and the transform can use one
            AttachDeviceManager(transform);

            ApplyCodecProperties(transform);

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            if (HasFrameRate) // a hint, given only where the stream says it
                mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaInput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_MixedInterlaceOrProgressive);
            mediaInput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));

            if (_isLowLatency)
            {
                transform.GetAttributes(out IMFAttributes attributes);
                attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);
            }
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput = null;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            mediaOutput.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, Width); 
            mediaOutput.SetUINT32(PInvoke.MF_MT_FIXED_SIZE_SAMPLES, 1);
            if (HasFrameRate) // a hint, given only where the stream says it
                mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaOutput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, MediaUtils.EncodeAttributeValue(1, 1));
            mediaOutput.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            mediaOutput.SetUINT32(PInvoke.MF_MT_SAMPLE_SIZE, Width * Height * 3 / 2);
            mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_MixedInterlaceOrProgressive); 
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }

        public override bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            if (SkipBVops && IsBVop(data))
                return false;
            return base.ProcessInput(data, timestamp);
        }

        /// <summary>
        /// Whether a sample's VOP is a B-VOP: its coding type, the first two bits after its start code (ISO/IEC 14496-2
        /// 6.2.5), is 2.
        /// </summary>
        public static bool IsBVop(ReadOnlySpan<byte> sample)
        {
            for (int i = 0; i + 4 < sample.Length; i++)
            {
                if (sample[i] == 0 && sample[i + 1] == 0 && sample[i + 2] == 1 && sample[i + 3] == 0xB6)
                    return (sample[i + 4] >> 6) == 2;
            }
            return false;
        }
    }
}
