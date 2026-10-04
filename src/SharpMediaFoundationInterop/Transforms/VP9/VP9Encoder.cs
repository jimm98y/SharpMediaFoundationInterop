using System;
using System.Collections.Generic;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms.VP9
{
    /// <summary>
    /// NV12 to VP9: each output sample one frame, as a VP9 sample in MP4 or WebM holds it. Windows has no VP9 encoder of
    /// its own: it comes with the VP9 Video Extensions from the Store, or from a GPU vendor's driver.
    /// </summary>
    public class VP9Encoder : VideoTransformBase
    {
        public const uint VP9_RES_MULTIPLE = 2;

        public override Guid InputFormat => PInvoke.MFVideoFormat_NV12;
        public override Guid OutputFormat => PInvoke.MFVideoFormat_VP90;

        public uint AvgBitrate { get; private set; }

        /// <summary>
        /// Encoder properties applied through ICodecAPI once the MFT exists but before the media
        /// types are set, which is where an encoder will still accept most of them. Anything the
        /// encoder does not support is reported through <see cref="CodecPropertyResults"/> rather
        /// than throwing, since support varies by vendor.
        /// </summary>
        public IDictionary<Guid, object> CodecProperties { get; } = new Dictionary<Guid, object>();

        /// <summary>What happened to each property in <see cref="CodecProperties"/>.</summary>
        public IList<(Guid Property, bool Supported, bool Applied)> CodecPropertyResults { get; } =
            new List<(Guid, bool, bool)>();

        /// <summary>The encoder's own settings interface, or null if it does not expose one.</summary>
        public ICodecApi CodecApi { get; private set; }

        public VP9Encoder(uint width, uint height, uint fpsNom, uint fpsDenom, uint avgBitrate = 8000000)
            : base(VP9_RES_MULTIPLE, width, height, fpsNom, fpsDenom)
        {
            this.AvgBitrate = avgBitrate;
        }

        protected override IMFTransform Create()
        {
            const uint streamId = 0;

            var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = InputFormat };
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = OutputFormat };

            IMFTransform transform = CreateTransform(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER, input, output);
            if (transform == null) throw new NotSupportedException($"Unsupported transform! Input: {InputFormat}, Output: {OutputFormat}");

            CodecApi = transform as ICodecApi;
            foreach (var property in CodecProperties)
            {
                bool supported = CodecApi != null && CodecApi.IsPropertySupported(property.Key);
                bool applied = supported && CodecApi.TrySetProperty(property.Key, property.Value);
                CodecPropertyResults.Add((property.Key, supported, applied));
            }

            IMFMediaType mediaOutput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaOutput));
            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, OutputFormat);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            mediaOutput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            mediaOutput.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, AvgBitrate);
            MediaUtils.Check(transform.SetOutputType(streamId, mediaOutput, 0));

            IMFMediaType mediaInput;
            MediaUtils.Check(PInvoke.MFCreateMediaType(out mediaInput));
            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, InputFormat);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, MediaUtils.EncodeAttributeValue(Width, Height));
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, MediaUtils.EncodeAttributeValue(FpsNom, FpsDenom));
            MediaUtils.Check(transform.SetInputType(streamId, mediaInput, 0));

            return transform;
        }
    }
}
