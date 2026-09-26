using SharpMediaFoundationInterop.Transforms.H265;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SharpSpatialVideo
{
    /// <summary>
    /// Drives the Media Foundation HEVC decoder over the rewritten single-layer stream. The
    /// decoder hands frames back in picture order, and because view 0 takes the even picture
    /// order counts and view 1 the odd ones, output alternates left eye, right eye.
    /// </summary>
    public sealed class SpatialDecoder : IDisposable
    {
        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        private readonly H265Decoder _decoder;
        private byte[] _buffer;

        public SpatialDecoder(uint codedWidth, uint codedHeight, uint fpsNom, uint fpsDenom,
            bool lowLatency = false, uint threads = 0)
        {
            _decoder = new H265Decoder(codedWidth, codedHeight, fpsNom, fpsDenom, lowLatency);
            if (threads > 0)
                _decoder.CodecProperties[SharpMediaFoundationInterop.Transforms.CodecApiProperties.DecoderWorkerThreads] = threads;
            _decoder.Initialize();

            // The decoder renegotiates its output type on the first frame; size generously so a
            // larger negotiated frame still fits.
            _buffer = new byte[codedWidth * codedHeight * 2];
        }

        public static byte[] ToAnnexB(IEnumerable<ArraySegment<byte>> nalus)
        {
            using var memory = new MemoryStream();
            foreach (var nalu in nalus)
            {
                memory.Write(StartCode, 0, StartCode.Length);
                memory.Write(nalu.Array, nalu.Offset, nalu.Count);
            }
            return memory.ToArray();
        }

        public void SendParameterSets(IEnumerable<byte[]> parameterSets)
        {
            _decoder.ProcessInput(ToAnnexB(parameterSets.Select(set => new ArraySegment<byte>(set))), 0);
        }

        /// <summary>
        /// Feeds one access unit and hands each frame that becomes available to
        /// <paramref name="onFrame"/> without copying it. The array is the decoder's own buffer: it
        /// is valid only for the duration of the call and is overwritten by the next frame, so a
        /// caller that needs to keep a frame has to copy it. At 1080p side by side a frame is over
        /// 6 MB, and copying every one of them was most of what a conversion allocated.
        /// </summary>
        public void DecodeInto(IEnumerable<ArraySegment<byte>> nalus, long timestamp, Action<byte[], long> onFrame)
        {
            _decoder.ProcessInput(ToAnnexB(nalus), timestamp);
            DrainInto(onFrame);
        }

        /// <summary>Drains the decoder into <paramref name="onFrame"/>, with the same lifetime rule.</summary>
        public void FlushInto(Action<byte[], long> onFrame)
        {
            // The queued frames have to be collected between the drain and the restart; restarting
            // first throws away everything the drain just produced. And ProcessOutput returns false
            // both for "nothing left" and for a format renegotiation, so a single false is not the
            // end: it is asked until it stays quiet.
            _decoder.BeginDrain();
            for (int quiet = 0; quiet < 4;)
                quiet = DrainInto(onFrame) > 0 ? 0 : quiet + 1;
            _decoder.EndDrain();
        }

        private int DrainInto(Action<byte[], long> onFrame)
        {
            int count = 0;
            while (true)
            {
                byte[] buffer = _buffer;
                if (!_decoder.ProcessOutput(ref buffer, out uint length, out long timestamp) || length == 0)
                    break;

                // ProcessOutput grows the buffer when a frame does not fit; the grown one is kept,
                // or every later frame allocates it again.
                _buffer = buffer;
                onFrame(buffer, timestamp);
                count++;
            }
            return count;
        }

        public void Dispose() => _decoder?.Dispose();
    }
}
