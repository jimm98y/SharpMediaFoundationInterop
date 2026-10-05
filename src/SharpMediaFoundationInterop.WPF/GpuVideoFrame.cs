using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A decoded frame on the GPU: an NV12 texture of the coded size, the picture its top left, given back to its pool when
    /// drawn - <see cref="IVideoSource.ReturnVideoFrame"/>.
    /// </summary>
    public sealed class GpuVideoFrame
    {
        private readonly GpuFramePool _pool;

        internal GpuVideoFrame(GpuFramePool pool, ID3D11Texture2D texture)
        {
            _pool = pool;
            Texture = texture;
            TexturePointer = Marshal.GetIUnknownForObject(texture);
        }

        /// <summary>
        /// The texture, of the source's device: NV12, made to be read by a video processor. Wrapped for the thread that made
        /// it, the source's; see <see cref="TexturePointer"/> for another.
        /// </summary>
        public ID3D11Texture2D Texture { get; }

        /// <summary>
        /// The texture itself, for a thread of another apartment to wrap for itself - the UI's, drawing it: see
        /// <see cref="Direct3DDevice"/>.
        /// </summary>
        public IntPtr TexturePointer { get; }

        /// <summary>Gives the frame back, to be decoded into again.</summary>
        public void Release() => _pool.Return(this);
    }

    /// <summary>
    /// The textures decoded frames are copied into out of a decoder's own: a decoder has a few surfaces only, which it
    /// waits for while a frame holds one - the frames queued to be shown, a group of pictures kept to be shown backwards -
    /// so each is copied out, on the GPU, and the decoder's let go of at once.
    /// </summary>
    internal sealed unsafe class GpuFramePool : IDisposable
    {
        private readonly Direct3DDevice _device;
        private readonly uint _width;
        private readonly uint _height;
        private readonly ConcurrentBag<GpuVideoFrame> _spare = new ConcurrentBag<GpuVideoFrame>();
        private readonly ConcurrentBag<GpuVideoFrame> _all = new ConcurrentBag<GpuVideoFrame>();
        private bool _disposed;

        public GpuFramePool(Direct3DDevice device, uint codedWidth, uint codedHeight)
        {
            _device = device;
            _width = codedWidth;
            _height = codedHeight;
        }

        /// <summary>
        /// A frame of the pool holding a copy of the picture a decoder handed out in <paramref name="sample"/>: of the slice of
        /// the texture array its buffer is of.
        /// </summary>
        public GpuVideoFrame CopyOf(IMFSample sample)
        {
            sample.GetBufferByIndex(0, out IMFMediaBuffer buffer);
            try
            {
                var dxgi = (IMFDXGIBuffer)buffer;
                dxgi.GetSubresourceIndex(out uint slice);
                var source = TextureOf(dxgi);
                try
                {
                    var frame = Rent();
                    var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = _width, bottom = _height, back = 1 };
                    _device.Context.CopySubresourceRegion(frame.Texture, 0, 0, 0, 0, source, slice, &box);
                    return frame;
                }
                finally
                {
                    Marshal.ReleaseComObject(source);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }

        /// <summary>A frame of the pool holding a copy of another's: for one kept in place of another, the same span of time.</summary>
        public void Copy(GpuVideoFrame from, GpuVideoFrame to)
        {
            _device.Context.CopyResource(to.Texture, from.Texture);
        }

        public GpuVideoFrame Rent()
        {
            if (_spare.TryTake(out var frame))
                return frame;

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_NV12,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                // what a video processor takes its input from
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_DECODER,
            };
            _device.Device.CreateTexture2D(desc, null, out ID3D11Texture2D texture);
            frame = new GpuVideoFrame(this, texture);
            _all.Add(frame);
            return frame;
        }

        public void Return(GpuVideoFrame frame)
        {
            if (!_disposed)
                _spare.Add(frame);
        }

        private static ID3D11Texture2D TextureOf(IMFDXGIBuffer buffer)
        {
            Guid iid = typeof(ID3D11Texture2D).GUID;
            void* resource;
            buffer.GetResource(&iid, &resource);
            try
            {
                return (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)resource);
            }
            finally
            {
                Marshal.Release((nint)resource);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            while (_all.TryTake(out var frame))
            {
                Marshal.Release(frame.TexturePointer);
                Marshal.ReleaseComObject(frame.Texture);
            }
            while (_spare.TryTake(out _)) { }
        }
    }
}
