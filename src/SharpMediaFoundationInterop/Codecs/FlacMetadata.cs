using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// The metadata blocks of a FLAC stream an encoder makes - what an MP4 file's 'dfLa' box holds - completed as Windows'
    /// encoder completes them: STREAMINFO of the stream's frame sizes, its total samples and the MD5 of its PCM, known once
    /// the stream has ended. An encoder that hands out its blocks before its frames does not know those yet.
    /// </summary>
    internal sealed class FlacMetadata : IDisposable
    {
        private readonly List<byte[]> _blocks = new List<byte[]>();
        private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        private readonly int _frameBytes;
        private byte[] _md5Final;
        private long _samples;
        private int _minFrame = int.MaxValue, _maxFrame;

        /// <param name="frameBytes">The bytes of a sample of every channel, of the PCM in.</param>
        public FlacMetadata(int frameBytes)
        {
            _frameBytes = frameBytes;
        }

        public bool HasBlocks => _blocks.Count > 0;

        /// <summary>
        /// PCM in: of the MD5, which FLAC's is of the samples as they are - signed, little-endian, interleaved, of the bytes
        /// their bits take - and of the total samples.
        /// </summary>
        public void AddInput(ReadOnlySpan<byte> pcm)
        {
            _md5.AppendData(pcm);
            _samples += pcm.Length / _frameBytes;
        }

        /// <summary>A frame out, of its size.</summary>
        public void AddFrame(int size)
        {
            _minFrame = Math.Min(_minFrame, size);
            _maxFrame = Math.Max(_maxFrame, size);
        }

        /// <summary>A metadata block, its header first: kept, in place of one of its type before, STREAMINFO first.</summary>
        public void AddBlock(ReadOnlySpan<byte> block)
        {
            int type = block[0] & 0x7F;
            _blocks.RemoveAll(b => (b[0] & 0x7F) == type);
            _blocks.Add(block.ToArray());
            _blocks.Sort((a, b) => (a[0] & 0x7F) == 0 ? -1 : (b[0] & 0x7F) == 0 ? 1 : 0);
        }

        /// <summary>
        /// The blocks of an encoder's header: 'fLaC', then the blocks, each of its header and length - of one buffer or of
        /// several, each block whole.
        /// </summary>
        public void AddHeader(ReadOnlySpan<byte> header)
        {
            if (header.StartsWith("fLaC"u8))
                header = header.Slice(4);
            while (header.Length >= 4)
            {
                int length = 4 + ((header[1] << 16) | (header[2] << 8) | header[3]);
                if (length > header.Length)
                    break;
                AddBlock(header.Slice(0, length));
                header = header.Slice(length);
            }
        }

        /// <summary>The stream has ended: no more goes in, and the MD5 is of all that did.</summary>
        public void End() => _md5Final ??= _md5.GetHashAndReset();

        /// <summary>The blocks, STREAMINFO first, the last marked so; STREAMINFO complete once the stream has ended. Null of none.</summary>
        public byte[] ToConfig()
        {
            if (_blocks.Count == 0)
                return null;
            var result = new List<byte>();
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = (byte[])_blocks[i].Clone();
                block[0] = (byte)((block[0] & 0x7F) | (i == _blocks.Count - 1 ? 0x80 : 0));
                if ((block[0] & 0x7F) == 0 && block.Length >= 4 + 34 && _md5Final != null && _samples > 0 && _maxFrame > 0)
                {
                    var info = block.AsSpan(4);
                    info[4] = (byte)(_minFrame >> 16); info[5] = (byte)(_minFrame >> 8); info[6] = (byte)_minFrame;
                    info[7] = (byte)(_maxFrame >> 16); info[8] = (byte)(_maxFrame >> 8); info[9] = (byte)_maxFrame;
                    // the total samples, 36 bits after the rate, channels and bits, then the MD5
                    info[13] = (byte)((info[13] & 0xF0) | (int)((_samples >> 32) & 0x0F));
                    info[14] = (byte)(_samples >> 24); info[15] = (byte)(_samples >> 16); info[16] = (byte)(_samples >> 8); info[17] = (byte)_samples;
                    _md5Final.CopyTo(info.Slice(18));
                }
                result.AddRange(block);
            }
            return result.ToArray();
        }

        public void Dispose() => _md5.Dispose();
    }
}
