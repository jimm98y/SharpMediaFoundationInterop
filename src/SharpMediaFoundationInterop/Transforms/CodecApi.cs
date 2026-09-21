using System;
using System.Runtime.InteropServices;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// ICodecAPI, the interface an encoder MFT exposes its own knobs through - GOP length, rate
    /// control mode, temporal layers, long term references. What a given encoder implements varies
    /// by vendor and by codec, so every property has to be asked about before it is set.
    ///
    /// Declared here rather than taken from the generated interop because the values are VARIANTs,
    /// and letting the runtime marshal those to and from object is far less work than handling
    /// VARIANT by hand.
    /// </summary>
    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICodecApi
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);

        [PreserveSig]
        int GetParameterRange(ref Guid api,
            [MarshalAs(UnmanagedType.Struct)] out object min,
            [MarshalAs(UnmanagedType.Struct)] out object max,
            [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);

        [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out int count);

        [PreserveSig]
        int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig]
        int GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig]
        int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    /// <summary>
    /// The encoder properties this library asks about. The values come from codecapi.h in the
    /// Windows SDK; the generated interop does not surface them.
    /// </summary>
    public static class CodecApiProperties
    {
        /// <summary>
        /// Number of temporal sub-layers (UINT32). A picture may only reference pictures whose
        /// temporal id is at most its own, so sub-layers partition the reference graph.
        /// </summary>
        public static readonly Guid TemporalLayerCount =
            new Guid("19caebff-b74d-4cfd-8c27-c2f9d97d5f52");

        /// <summary>How many long term reference frames the encoder should keep (UINT32).</summary>
        public static readonly Guid LtrBufferControl =
            new Guid("a4a0e93d-4cbc-444c-89f4-826d310e92a7");

        /// <summary>Marks a frame as a long term reference (UINT32).</summary>
        public static readonly Guid MarkLtrFrame =
            new Guid("e42f4748-a06d-4ef9-8cea-3d05fde3bd3b");

        /// <summary>Which long term references the next frame may predict from (UINT32).</summary>
        public static readonly Guid UseLtrFrame =
            new Guid("00752db8-55f7-4f80-895b-27639195f2ad");

        /// <summary>Distance between key frames (UINT32).</summary>
        public static readonly Guid GopSize =
            new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1");

        /// <summary>Constant bitrate, variable bitrate, quality and so on (UINT32).</summary>
        public static readonly Guid RateControlMode =
            new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");

        /// <summary>Values for <see cref="RateControlMode"/>, from eAVEncCommonRateControlMode.</summary>
        public static class RateControlModes
        {
            public const uint Cbr = 0;
            public const uint PeakConstrainedVbr = 1;
            public const uint UnconstrainedVbr = 2;
            public const uint Quality = 3;
        }

        /// <summary>
        /// Target quality, 0 to 100, used when the rate control mode is Quality. Quality mode
        /// carries no bitrate state between pictures, which matters when two encoder runs have to
        /// agree with each other.
        /// </summary>
        public static readonly Guid Quality =
            new Guid("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");

        /// <summary>
        /// How many threads the encoder may use (UINT32). Each keeps its own working buffers, so
        /// this trades speed against memory.
        /// </summary>
        public static readonly Guid NumWorkerThreads =
            new Guid("b0c8bf60-16f7-4951-a30b-1db1609293d6");

        /// <summary>How many threads a decoder may use (UINT32).</summary>
        public static readonly Guid DecoderWorkerThreads =
            new Guid("9561c3e8-ea9e-4435-9b1e-a93e691894d8");

        /// <summary>Number of reference pictures the encoder may keep (UINT32).</summary>
        public static readonly Guid MaxNumRefFrame =
            new Guid("964829ed-94f9-43b4-b74d-ef40944b69a0");

        /// <summary>Number of B pictures between reference pictures (UINT32).</summary>
        public static readonly Guid BPictureCount =
            new Guid("8d390aac-dc5c-4200-b57f-814d04bab74c");
    }

    /// <summary>Asking an encoder what it supports, without an exception per unsupported property.</summary>
    public static class CodecApiExtensions
    {
        /// <summary>True when the encoder implements this property at all.</summary>
        public static bool IsPropertySupported(this ICodecApi codec, Guid property) =>
            codec != null && codec.IsSupported(ref property) == 0;

        /// <summary>True when the property can still be changed in the encoder's current state.</summary>
        public static bool IsPropertyModifiable(this ICodecApi codec, Guid property) =>
            codec != null && codec.IsModifiable(ref property) == 0;

        public static bool TryGetProperty(this ICodecApi codec, Guid property, out object value)
        {
            value = null;
            if (codec == null)
                return false;

            try
            {
                return codec.GetValue(ref property, out value) == 0;
            }
            catch (Exception)
            {
                return false;   // an encoder that does not implement a property can throw rather than fail
            }
        }

        public static bool TrySetProperty(this ICodecApi codec, Guid property, object value)
        {
            if (codec == null)
                return false;

            try
            {
                return codec.SetValue(ref property, ref value) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
