using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// Draws frames into a BGRA texture shared with other devices - Direct3D 9's, for WPF's D3DImage - on the GPU: an NV12
    /// frame uploaded as it is and converted by the GPU's video processor, a BGRA one uploaded as it is, a BGR24 one widened
    /// to BGRA on the way.
    /// </summary>
    internal sealed unsafe class D3D11FrameRenderer : IDisposable
    {
        private readonly VideoInfo _info;
        private readonly uint _width;    // of the picture
        private readonly uint _height;

        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private ID3D11Texture2D _output;
        private ID3D11Query _done;

        // NV12: uploaded to the staging texture, copied to the input, converted from it into the output
        private ID3D11Texture2D _nv12Staging;
        private ID3D11Texture2D _nv12Input;
        private ID3D11VideoDevice _videoDevice;
        private ID3D11VideoContext _videoContext;
        private ID3D11VideoProcessorEnumerator _enumerator;
        private ID3D11VideoProcessor _processor;
        private ID3D11VideoProcessorInputView _inputView;
        private ID3D11VideoProcessorOutputView _outputView;

        // BGR24: widened to BGRA here first
        private byte[] _bgra;

        /// <summary>The texture's handle, for another device to open it by.</summary>
        public IntPtr SharedHandle { get; private set; }

        public uint Width => _width;
        public uint Height => _height;

        /// <summary>The texture frames are drawn into, for a test to read back.</summary>
        internal ID3D11Texture2D Output => _output;
        internal ID3D11Device Device => _device;
        internal ID3D11DeviceContext Context => _context;

        public D3D11FrameRenderer(VideoInfo info)
        {
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _width = info.OriginalWidth;
            _height = info.OriginalHeight;

            var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                null, PInvoke.D3D11_SDK_VERSION, out _device, out _, out _context);
            Marshal.ThrowExceptionForHR(hr);

            // drawn on the UI thread, and the device may be asked of from elsewhere: one call at a time into it
            ((ID3D10Multithread)_device).SetMultithreadProtected(true);

            var outputDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED,
            };
            _device.CreateTexture2D(outputDesc, null, out _output);
            HANDLE shared;
            ((IDXGIResource)_output).GetSharedHandle(&shared);
            SharedHandle = (IntPtr)shared.Value;

            _device.CreateQuery(new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT }, out _done);

            if (info.PixelFormat == PixelFormat.NV12)
                CreateVideoProcessor();
            else if (info.PixelFormat == PixelFormat.BGR24)
                _bgra = new byte[_width * _height * 4];
        }

        private void CreateVideoProcessor()
        {
            uint codedWidth = _info.Width, codedHeight = _info.Height;
            var nv12Desc = new D3D11_TEXTURE2D_DESC
            {
                Width = codedWidth,
                Height = codedHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_NV12,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            _device.CreateTexture2D(nv12Desc, null, out _nv12Staging);
            nv12Desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
            nv12Desc.CPUAccessFlags = 0;
            // what a decoder decodes into, which is what a video processor takes in: as a shader resource it is refused
            nv12Desc.BindFlags = D3D11_BIND_FLAG.D3D11_BIND_DECODER;
            _device.CreateTexture2D(nv12Desc, null, out _nv12Input);

            _videoDevice = (ID3D11VideoDevice)_device;
            _videoContext = (ID3D11VideoContext)_context;

            var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
            {
                InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
                InputWidth = codedWidth,
                InputHeight = codedHeight,
                OutputWidth = _width,
                OutputHeight = _height,
                Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
            };
            _videoDevice.CreateVideoProcessorEnumerator(content, out _enumerator);
            _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);

            var inputViewDesc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
            {
                ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
            };
            _videoDevice.CreateVideoProcessorInputView(_nv12Input, _enumerator, inputViewDesc, out _inputView);

            var outputViewDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
            {
                ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D,
            };
            _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator, outputViewDesc, out _outputView);

            // the picture is the top left of the coded frame; drawn over the whole of the output
            var source = new RECT(0, 0, (int)_width, (int)_height);
            var target = new RECT(0, 0, (int)_width, (int)_height);
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, &source);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, &target);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, &target);

            // studio range YCbCr in, of BT.709 for HD and BT.601 below, as a decoder of either makes it; full range RGB out
            var inputSpace = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE();
            inputSpace._bitfield = _height >= 720 ? 0b100u : 0u; // YCbCr_Matrix: 1 for BT.709
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, inputSpace);
            var outputSpace = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE();
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, outputSpace);

            _streams = new[] { new D3D11_VIDEO_PROCESSOR_STREAM { Enable = true, pInputSurface = _inputView } };
        }

        // the one stream converted, made once: an array a frame otherwise
        private D3D11_VIDEO_PROCESSOR_STREAM[] _streams;

        /// <summary>
        /// Draws a frame, of the format the source said, into the shared texture, and waits for the GPU to have done it: the
        /// other device reading it next does not wait for this one.
        /// </summary>
        public void Draw(byte[] frame)
        {
            switch (_info.PixelFormat)
            {
                case PixelFormat.NV12:
                    UploadNV12(frame);
                    _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, _streams);
                    break;

                case PixelFormat.BGRA32:
                    fixed (byte* data = frame)
                        _context.UpdateSubresource(_output, 0, null, data, _width * 4, 0);
                    break;

                default:
                    WidenToBgra(frame);
                    fixed (byte* data = _bgra)
                        _context.UpdateSubresource(_output, 0, null, data, _width * 4, 0);
                    break;
            }

            _context.End(_done);
            _context.Flush();
            // an event query's data is whether the GPU has got to it: false, and not written, until then
            BOOL finished = false;
            while (true)
            {
                _context.GetData(_done, &finished, (uint)sizeof(BOOL), 0);
                if (finished)
                    break;
                System.Threading.Thread.Yield();
            }
        }

        /// <summary>The frame's two planes into the staging texture, a row at a time - its rows may be wider - and on to the input.</summary>
        private void UploadNV12(byte[] frame)
        {
            uint codedWidth = _info.Width, codedHeight = _info.Height;
            D3D11_MAPPED_SUBRESOURCE mapped;
            _context.Map(_nv12Staging, 0, D3D11_MAP.D3D11_MAP_WRITE, 0, &mapped);
            try
            {
                fixed (byte* source = frame)
                {
                    byte* target = (byte*)mapped.pData;
                    // the luma's rows, then the chroma's, half as many, the planes one after another at the same pitch
                    for (uint y = 0; y < codedHeight * 3 / 2; y++)
                        Buffer.MemoryCopy(source + (long)y * codedWidth, target + (long)y * mapped.RowPitch, codedWidth, codedWidth);
                }
            }
            finally
            {
                _context.Unmap(_nv12Staging, 0);
            }
            _context.CopyResource(_nv12Input, _nv12Staging);
        }

        private void WidenToBgra(byte[] frame)
        {
            fixed (byte* source = frame)
            fixed (byte* target = _bgra)
            {
                long pixels = (long)_width * _height;
                for (long i = 0; i < pixels; i++)
                {
                    target[i * 4] = source[i * 3];
                    target[i * 4 + 1] = source[i * 3 + 1];
                    target[i * 4 + 2] = source[i * 3 + 2];
                    target[i * 4 + 3] = 255;
                }
            }
        }

        public void Dispose()
        {
            Release(ref _outputView);
            Release(ref _inputView);
            Release(ref _processor);
            Release(ref _enumerator);
            Release(ref _nv12Input);
            Release(ref _nv12Staging);
            Release(ref _done);
            Release(ref _output);
            _videoContext = null;
            _videoDevice = null;
            Release(ref _context);
            Release(ref _device);
        }

        private static void Release<T>(ref T com) where T : class
        {
            if (com != null && Marshal.IsComObject(com))
                Marshal.ReleaseComObject(com);
            com = null;
        }
    }
}
