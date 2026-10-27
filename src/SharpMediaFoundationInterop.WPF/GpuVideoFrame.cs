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

    /// <summary>
    /// Two eyes' frames decoded on the GPU put side by side there, into frames of a pool of its own: each eye's picture copied
    /// into its half over black, as <see cref="StereoMediaSource"/> composes frames on the CPU. Of the thread frames are
    /// asked for on: the eyes' textures, made on their sources' threads, are wrapped for it.
    /// </summary>
    internal sealed unsafe class GpuSideBySide : IDisposable
    {
        private readonly Direct3DDevice _device;
        private readonly GpuFramePool _pool;
        private readonly ID3D11Texture2D _black;
        private readonly System.Collections.Generic.Dictionary<IntPtr, ID3D11Texture2D> _eyes = new System.Collections.Generic.Dictionary<IntPtr, ID3D11Texture2D>();
        private ID3D11Texture2D _staging;
        private uint _stagingWidth, _stagingHeight;

        /// <param name="black">The black of the luma, of the stream's range: 16 of the limited, 0 of the full.</param>
        public GpuSideBySide(Direct3DDevice device, uint width, uint height, byte black)
        {
            _device = device;
            _pool = new GpuFramePool(device, width, height);

            // a frame of black, which each frame is made of before the eyes go in: the luma, then the neutral chroma
            var pixels = new byte[width * height * 3 / 2];
            pixels.AsSpan(0, (int)(width * height)).Fill(black);
            pixels.AsSpan((int)(width * height)).Fill(128);
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_NV12,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            };
            fixed (byte* data = pixels)
            {
                // of NV12, the chroma's rows after the luma's, of the same pitch
                var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = width };
                device.Device.CreateTexture2D(desc, initial, out _black);
            }
        }

        /// <summary>
        /// The two eyes side by side: the left's picture at the left, the right's from <paramref name="rightX"/>, each of its
        /// size; a missing right eye black. Each picture's size is rounded up to even, within its frame: NV12 is copied in
        /// pairs of pixels.
        /// </summary>
        public GpuVideoFrame Compose(GpuVideoFrame left, VideoInfo leftInfo, uint leftWidth, uint leftHeight,
            GpuVideoFrame right, VideoInfo rightInfo, uint rightX, uint rightWidth, uint rightHeight)
        {
            var frame = _pool.Rent();
            var context = _device.Context;
            context.CopyResource(frame.Texture, _black);
            Copy(context, frame, left, leftInfo, 0, leftWidth, leftHeight);
            if (right != null)
                Copy(context, frame, right, rightInfo, rightX, rightWidth, rightHeight);
            return frame;
        }

        private void Copy(ID3D11DeviceContext context, GpuVideoFrame target, GpuVideoFrame eye, VideoInfo info, uint x, uint width, uint height)
        {
            var box = new D3D11_BOX
            {
                left = 0,
                top = 0,
                front = 0,
                right = Math.Min((width + 1) & ~1u, info.Width),
                bottom = Math.Min((height + 1) & ~1u, info.Height),
                back = 1,
            };
            context.CopySubresourceRegion(target.Texture, 0, x, 0, 0, Wrapped(eye), 0, &box);
        }

        /// <summary>
        /// An eye's frame read back into the CPU's memory, as its source hands frames out there: NV12 of its coded size, of
        /// rows of no padding, in an array of the shared pool. Where only one eye is decoded on the GPU, the pair is composed
        /// on the CPU.
        /// </summary>
        public byte[] Download(GpuVideoFrame eye, VideoInfo info)
        {
            uint width = info.Width, height = info.Height;
            if (_staging == null || _stagingWidth != width || _stagingHeight != height)
            {
                if (_staging != null)
                    Marshal.ReleaseComObject(_staging);
                var desc = new D3D11_TEXTURE2D_DESC
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_NV12,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                };
                _device.Device.CreateTexture2D(desc, null, out _staging);
                (_stagingWidth, _stagingHeight) = (width, height);
            }

            var context = _device.Context;
            context.CopyResource(_staging, Wrapped(eye));
            byte[] frame = System.Buffers.ArrayPool<byte>.Shared.Rent((int)(width * height * 3 / 2));
            D3D11_MAPPED_SUBRESOURCE mapped;
            context.Map(_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            try
            {
                // the luma's rows, then the chroma's, which start after the luma's of the mapped pitch
                byte* source = (byte*)mapped.pData;
                for (uint row = 0; row < height * 3 / 2; row++)
                    new ReadOnlySpan<byte>(source + row * mapped.RowPitch, (int)width).CopyTo(frame.AsSpan((int)(row * width)));
            }
            finally
            {
                context.Unmap(_staging, 0);
            }
            return frame;
        }

        /// <summary>An eye's texture wrapped for this thread, once: its pool's textures are used again and again.</summary>
        private ID3D11Texture2D Wrapped(GpuVideoFrame eye)
        {
            if (!_eyes.TryGetValue(eye.TexturePointer, out var texture))
            {
                texture = (ID3D11Texture2D)Direct3DDevice.ForThisThread(eye.TexturePointer);
                _eyes.Add(eye.TexturePointer, texture);
            }
            return texture;
        }

        public void Dispose()
        {
            _pool.Dispose();
            Marshal.ReleaseComObject(_black);
            if (_staging != null)
                Marshal.ReleaseComObject(_staging);
            _eyes.Clear();
        }
    }
}
