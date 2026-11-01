using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// ICodecAPI, the interface an encoder MFT exposes its own knobs through - GOP length, rate
    /// control mode, temporal layers, long term references. What a given encoder implements varies
    /// by vendor and by codec, so every property has to be asked about before it is set.
    ///
    /// Declared here, of the COM source generators, its values VARIANTs of <see cref="CodecApiValue"/>:
    /// the numbers and flags a codec's properties are, read and written by hand, not marshalled by the
    /// runtime - which does not, trimmed or compiled ahead of time. Only the methods up to SetValue, in
    /// the order of the interface's table.
    /// </summary>
    [GeneratedComInterface]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [SupportedOSPlatform("windows10.0.17763.0")]
    public unsafe partial interface ICodecApi
    {
        [PreserveSig] int IsSupported(Guid* api);
        [PreserveSig] int IsModifiable(Guid* api);
        [PreserveSig] int GetParameterRange(Guid* api, CodecApiValue* min, CodecApiValue* max, CodecApiValue* steppingDelta);
        [PreserveSig] int GetParameterValues(Guid* api, CodecApiValue** values, uint* count);
        [PreserveSig] int GetDefaultValue(Guid* api, CodecApiValue* value);
        [PreserveSig] int GetValue(Guid* api, CodecApiValue* value);
        [PreserveSig] int SetValue(Guid* api, CodecApiValue* value);
    }

    /// <summary>
    /// A VARIANT of a codec property's value: its type, and a number of up to 64 bits after it - of 24 bytes on 64 bits, of
    /// 16 on 32, as VARIANT is. Of the types a codec's properties are; another read as no value.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CodecApiValue
    {
        private const ushort VT_EMPTY = 0, VT_I2 = 2, VT_I4 = 3, VT_R8 = 5, VT_BOOL = 11, VT_I1 = 16, VT_UI1 = 17, VT_UI2 = 18,
            VT_UI4 = 19, VT_I8 = 20, VT_UI8 = 21, VT_INT = 22, VT_UINT = 23;

        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        // the value's 8 bytes - the union's first, whatever its size
        private long _value;
        private nint _record;

        /// <summary>A value of a number or a flag; null of another type.</summary>
        public static CodecApiValue? Of(object value)
        {
            var variant = new CodecApiValue();
            switch (value)
            {
                case bool b: variant.Type = VT_BOOL; variant._value = b ? -1 : 0; break;
                case byte n: variant.Type = VT_UI1; variant._value = n; break;
                case sbyte n: variant.Type = VT_I1; variant._value = n; break;
                case ushort n: variant.Type = VT_UI2; variant._value = n; break;
                case short n: variant.Type = VT_I2; variant._value = n; break;
                case uint n: variant.Type = VT_UI4; variant._value = n; break;
                case int n: variant.Type = VT_I4; variant._value = n; break;
                case ulong n: variant.Type = VT_UI8; variant._value = unchecked((long)n); break;
                case long n: variant.Type = VT_I8; variant._value = n; break;
                case double d: variant.Type = VT_R8; variant._value = BitConverter.DoubleToInt64Bits(d); break;
                default: return null;
            }
            return variant;
        }

        /// <summary>The value, of its type; null of a type of no number or flag.</summary>
        public object Value => Type switch
        {
            VT_BOOL => (short)_value != 0,
            VT_UI1 => (byte)_value,
            VT_I1 => (sbyte)_value,
            VT_UI2 => (ushort)_value,
            VT_I2 => (short)_value,
            VT_UI4 or VT_UINT => (uint)_value,
            VT_I4 or VT_INT => (int)_value,
            VT_UI8 => unchecked((ulong)_value),
            VT_I8 => _value,
            VT_R8 => BitConverter.Int64BitsToDouble(_value),
            _ => null,
        };
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

        /// <summary>Quantiser for constant QP coding (UINT64, the QP in bits 0-15).</summary>
        public static readonly Guid EncodeQp =
            new Guid("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");

        /// <summary>
        /// Quantiser per picture type (UINT64): I in bits 0-15, P in 16-31, B in 32-47. Giving them
        /// all the same value stops an encoder spending more on its key frames than on the rest.
        /// </summary>
        public static readonly Guid EncodeFrameTypeQp =
            new Guid("aa70b610-e03f-450c-ad07-07314e639ce7");

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
    [SupportedOSPlatform("windows10.0.17763.0")]
    public static class CodecApiExtensions
    {
        /// <summary>True when the encoder implements this property at all.</summary>
        public static unsafe bool IsPropertySupported(this ICodecApi codec, Guid property) =>
            codec != null && codec.IsSupported(&property) == 0;

        /// <summary>True when the property can still be changed in the encoder's current state.</summary>
        public static unsafe bool IsPropertyModifiable(this ICodecApi codec, Guid property) =>
            codec != null && codec.IsModifiable(&property) == 0;

        public static unsafe bool TryGetProperty(this ICodecApi codec, Guid property, out object value)
        {
            value = null;
            if (codec == null)
                return false;

            try
            {
                CodecApiValue variant = default;
                if (codec.GetValue(&property, &variant) != 0)
                    return false;
                value = variant.Value;
                return value != null;
            }
            catch (Exception)
            {
                return false;   // an encoder that does not implement a property can throw rather than fail
            }
        }

        public static unsafe bool TrySetProperty(this ICodecApi codec, Guid property, object value)
        {
            // of a value of no number or flag, nothing set
            if (codec == null || CodecApiValue.Of(value) is not CodecApiValue variant)
                return false;

            try
            {
                return codec.SetValue(&property, &variant) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
