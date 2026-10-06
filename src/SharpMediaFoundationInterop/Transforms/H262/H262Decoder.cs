using System.Runtime.Versioning;
using System;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.H262
{
    /// <summary>
    /// H.262, MPEG-2 video - and MPEG-1 video, which the decoder takes as MPEG-2 - decoded by Media Foundation's MPEG-2
    /// decoder: the MPEG-2 Video Extension, where Windows does not have one of its own. Its sequence header comes in band,
    /// before the first picture.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class H262Decoder : VideoTransformBase
    {
        /// <summary>Its pictures are coded in macroblocks of 16 by 16.</summary>
        public const uint H262_RES_MULTIPLE = 16;

        public override Guid InputFormat => PInvoke.MFVideoFormat_MPEG2;
        public override Guid OutputFormat => PInvoke.MFVideoFormat_NV12;

        private bool _isLowLatency = false;

        public H262Decoder(uint width, uint height, uint fpsNom, uint fpsDenom, bool isLowLatency = false)
          : base(H262_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
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

        /// <summary>
        /// Flushes the decoder, and drains it of the nothing it then holds: flushed while playing, the MPEG-2 decoder hands
        /// out nothing on the drain after - the first key frame, played by key frames alone - unless drained in between.
        /// </summary>
        public override void Flush()
        {
            base.Flush();
            BeginDrain();
            EndDrain();
        }

        /// <summary>
        /// Restarts the stream, as for any transform, and sets the decoder's types again: drained, the MPEG-2 decoder hands
        /// out nothing more - a key frame after a key frame, played by them alone - until they are.
        /// </summary>
        public override void EndDrain()
        {
            base.EndDrain();

            const uint streamId = 0;
            MediaUtils.Check(_transform.GetInputCurrentType(streamId, out IMFMediaType input));
            MediaUtils.Check(_transform.GetOutputCurrentType(streamId, out IMFMediaType output));
            MediaUtils.Check(_transform.SetInputType(streamId, input, 0));
            MediaUtils.Check(_transform.SetOutputType(streamId, output, 0));
        }
    }
}
