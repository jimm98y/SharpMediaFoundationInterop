using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpSpatialVideo
{
    /// <summary>A decoded frame of the rewritten stream, tagged with the view it belongs to.</summary>
    public sealed class DecodedFrame
    {
        public int Index { get; set; }
        public byte[] Nv12 { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }

        /// <summary>Presentation time as reported by the decoder, in 100ns units.</summary>
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// Drives the Media Foundation HEVC decoder over the rewritten single-layer stream. The
    /// decoder hands frames back in picture order, and because view 0 takes the even picture
    /// order counts and view 1 the odd ones, output alternates left eye, right eye.
    /// </summary>
    public sealed class SpatialDecoder : IDisposable
    {
        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        private readonly H265Decoder _decoder;
        private readonly byte[] _buffer;

        public uint CodedWidth { get; }
        public uint CodedHeight { get; }

        public SpatialDecoder(uint codedWidth, uint codedHeight, uint fpsNom, uint fpsDenom)
        {
            CodedWidth = codedWidth;
            CodedHeight = codedHeight;

            _decoder = new H265Decoder(codedWidth, codedHeight, fpsNom, fpsDenom);
            _decoder.Initialize();

            // The decoder renegotiates its output type on the first frame; size generously so a
            // larger negotiated frame still fits.
            _buffer = new byte[codedWidth * codedHeight * 2];
        }

        public static byte[] ToAnnexB(IEnumerable<byte[]> nalus)
        {
            using var memory = new MemoryStream();
            foreach (var nalu in nalus)
            {
                memory.Write(StartCode, 0, StartCode.Length);
                memory.Write(nalu, 0, nalu.Length);
            }
            return memory.ToArray();
        }

        public int RejectedInputs { get; private set; }

        public void SendParameterSets(IEnumerable<byte[]> parameterSets)
        {
            if (!_decoder.ProcessInput(ToAnnexB(parameterSets), 0))
                RejectedInputs++;
        }

        /// <summary>Feeds one picture and returns whatever frames became available.</summary>
        public IEnumerable<DecodedFrame> Decode(byte[] nalu, long timestamp)
        {
            if (!_decoder.ProcessInput(ToAnnexB(new[] { nalu }), timestamp))
                RejectedInputs++;
            return Drain();
        }

        public IEnumerable<DecodedFrame> Flush()
        {
            // The queued frames have to be collected between the drain and the restart; restarting
            // first throws away everything the drain just produced.
            _decoder.BeginDrain();

            // ProcessOutput returns false both for "nothing left" and for a format renegotiation,
            // so a single false is not the end. Keep asking until it stays quiet.
            var frames = new List<DecodedFrame>();
            for (int quiet = 0; quiet < 4; )
            {
                var batch = Drain();
                if (batch.Count == 0) quiet++;
                else { frames.AddRange(batch); quiet = 0; }
            }

            _decoder.EndDrain();
            return frames;
        }

        private List<DecodedFrame> Drain()
        {
            var frames = new List<DecodedFrame>();
            while (true)
            {
                byte[] buffer = _buffer;
                if (!_decoder.ProcessOutput(ref buffer, out uint length, out long timestamp) || length == 0)
                    break;

                var frame = new byte[length];
                Buffer.BlockCopy(buffer, 0, frame, 0, (int)length);
                frames.Add(new DecodedFrame
                {
                    Nv12 = frame,
                    Width = CodedWidth,
                    Height = CodedHeight,
                    Timestamp = timestamp,
                });
            }
            return frames;
        }

        public void Dispose() => _decoder?.Dispose();
    }
}
