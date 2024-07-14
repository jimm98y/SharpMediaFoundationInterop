using System;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Colors
{
    /// <summary>
    /// Converts among different color formats.
    /// </summary>
    public class ColorConverter : VideoTransformBase
    {
        private Guid _sourceFormat;
        private Guid _targetFormat;

        public ColorConverter(Guid sourceFormat, Guid targetFormat, uint width, uint height) : base(width, height)
        {
            _sourceFormat = sourceFormat;
            _targetFormat = targetFormat;
        }

        protected override IMFTransform Create()
        {
            const int streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = _sourceFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = _targetFormat };

            IMFTransform transform = MFTUtils.CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_PROCESSOR, MFT_ENUM_FLAG.MFT_ENUM_FLAG_ALL, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {_sourceFormat}, Output: {_targetFormat}");

            IMFMediaType mediaInput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, _sourceFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            MFTUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            IMFMediaType mediaOutput;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, _targetFormat);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MFTUtils.EncodeAttributeValue(Width, Height));
            MFTUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            return transform;
        }
    }
}
