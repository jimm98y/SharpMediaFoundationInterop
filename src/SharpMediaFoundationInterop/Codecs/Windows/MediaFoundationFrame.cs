using SharpMediaFoundationInterop.Utils;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A frame of a Media Foundation transform's: the sample it handed out, and of a sample of the GPU's memory, the texture
    /// its buffer is of.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    internal sealed unsafe class MediaFoundationFrame : DecodedFrame
    {
        private IMFSample _sample;
        private IntPtr _texture;
        private readonly int _textureIndex;

        /// <summary>The frame of <paramref name="sample"/>, whose reference it takes: the caller lets go of the sample no more.</summary>
        public MediaFoundationFrame(IMFSample sample)
        {
            _sample = sample;

            sample.GetBufferByIndex(0, out IMFMediaBuffer buffer);
            try
            {
                if (buffer is IMFDXGIBuffer dxgi)
                {
                    dxgi.GetSubresourceIndex(out uint slice);
                    _textureIndex = (int)slice;

                    Guid iid = typeof(ID3D11Texture2D).GUID;
                    void* resource;
                    dxgi.GetResource(&iid, &resource);
                    _texture = (IntPtr)resource;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }

        public override bool IsOnGpu => _texture != IntPtr.Zero;

        public override IntPtr Texture => _texture;

        public override int TextureIndex => _textureIndex;

        private protected override bool CopyToCore(byte[] buffer, out uint length)
        {
            _sample.ConvertToContiguousBuffer(out IMFMediaBuffer contiguous);
            try
            {
                return MediaUtils.CopyBuffer(contiguous, buffer, out length);
            }
            finally
            {
                Marshal.ReleaseComObject(contiguous);
            }
        }

        private protected override void Release(bool disposing)
        {
            IntPtr texture = Interlocked.Exchange(ref _texture, IntPtr.Zero);
            if (texture != IntPtr.Zero)
                Marshal.Release(texture);

            if (disposing && _sample != null)
            {
                Marshal.ReleaseComObject(_sample);
                _sample = null;
            }
        }
    }
}
