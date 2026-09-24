using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Transforms.H265;

namespace SharpSpatialVideo
{
    /// <summary>
    /// How an encoder controls its rate, as written on the command line: "cbr" (the bitrate it was
    /// constructed with), "quality:N" (the encoder's quality mode, 0 to 100) or "qp:N" (a constant
    /// quantiser, the same for every picture type).
    ///
    /// Constant bitrate is the Media Foundation encoder's default, and it spends far more on each
    /// key frame than on the pictures between them. Fine texture comes back sharp at every key frame
    /// and softens over the second after, on a steady beat - measured at 1.5 dB up at each key
    /// frame and falling from there. A constant quantiser, the same for I and P pictures, codes
    /// them all alike and the beat goes: flat through the key frames, at the same file size.
    /// </summary>
    public static class EncoderRateControl
    {
        public static void Apply(H265Encoder encoder, string rateControl)
        {
            var parts = rateControl.Split(':');
            switch (parts[0])
            {
                case "cbr":
                    break;

                case "quality":
                    encoder.CodecProperties[CodecApiProperties.RateControlMode] = CodecApiProperties.RateControlModes.Quality;
                    encoder.CodecProperties[CodecApiProperties.Quality] = uint.Parse(parts[1]);
                    break;

                case "qp":
                    ulong qp = ulong.Parse(parts[1]);
                    encoder.CodecProperties[CodecApiProperties.RateControlMode] = CodecApiProperties.RateControlModes.Quality;
                    encoder.CodecProperties[CodecApiProperties.EncodeQp] = qp;
                    encoder.CodecProperties[CodecApiProperties.EncodeFrameTypeQp] = qp | (qp << 16) | (qp << 32);
                    break;

                default:
                    throw new System.ArgumentException($"unknown rate control '{rateControl}'; use cbr, quality:N or qp:N");
            }
        }

        /// <summary>Warns about any property the encoder did not take, so a setting cannot fail silently.</summary>
        public static void ReportRejected(H265Encoder encoder)
        {
            foreach (var result in encoder.CodecPropertyResults)
                if (!result.Applied)
                    System.Console.WriteLine($"  [encoder] property {result.Property} not applied (supported: {result.Supported})");
        }
    }
}
