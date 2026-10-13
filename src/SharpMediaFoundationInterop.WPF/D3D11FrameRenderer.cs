using System;
using System.Runtime.InteropServices;
using System.Text;
using SharpMediaFoundationInterop.Codecs;
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
    /// Draws frames into a BGRA texture shared with other devices - Direct3D 9's, for WPF's D3DImage - on the GPU: a frame
    /// as decoded - NV12, or of any <see cref="VideoInfo.FrameFormat"/> - uploaded as it is and converted by a pixel shader,
    /// of the stream's colour; one decoded on the GPU converted where it is by the GPU's video processor; a BGRA one
    /// uploaded as it is, a BGR24 one widened to BGRA on the way. That is the whole frame; or, of a <see cref="VideoView"/>, one eye's half of it copied out, or a
    /// view into a spherical video drawn from it by a pixel shader - into a texture of the view's own size.
    /// </summary>
    internal sealed unsafe class D3D11FrameRenderer : IDisposable
    {
        private readonly VideoInfo _info;
        private readonly uint _width;    // of the picture
        private readonly uint _height;
        private readonly uint _outputWidth;
        private readonly uint _outputHeight;

        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private ID3D11Query _done;

        // The frame, converted to BGRA; and what is shown, shared: the same texture, where the whole frame is shown.
        private ID3D11Texture2D _picture;
        private ID3D11Texture2D _output;

        // decoded on the GPU: converted where it is, by the video processor, into the picture
        private ID3D11VideoDevice _videoDevice;
        private ID3D11VideoContext _videoContext;
        private ID3D11VideoProcessorEnumerator _enumerator;
        private ID3D11VideoProcessor _processor;
        private ID3D11VideoProcessorOutputView _outputView;

        // BGR24: widened to BGRA here first
        private byte[] _bgra;

        /// <summary>The texture's handle, for another device to open it by.</summary>
        public IntPtr SharedHandle { get; private set; }

        /// <summary>The size of what is shown: the shared texture's.</summary>
        public uint Width => _outputWidth;
        public uint Height => _outputHeight;

        /// <summary>Whether the whole frame is shown, as it is: otherwise a view of it, of <see cref="Draw(object, in VideoView)"/>.</summary>
        public bool IsWholeFrame { get; }

        /// <summary>The texture frames are drawn into, for a test to read back.</summary>
        internal ID3D11Texture2D Output => _output;
        internal ID3D11Device Device => _device;
        internal ID3D11DeviceContext Context => _context;

        /// <summary>The picture drawn last - the whole frame, converted - read back of the GPU as BGRA, top-down: for a test.</summary>
        internal byte[] ReadPicture()
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            _device.CreateTexture2D(desc, null, out var staging);
            try
            {
                _context.CopyResource(staging, _picture);
                D3D11_MAPPED_SUBRESOURCE mapped;
                _context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                var pixels = new byte[_width * _height * 4];
                fixed (byte* target = pixels)
                {
                    for (uint y = 0; y < _height; y++)
                        Buffer.MemoryCopy((byte*)mapped.pData + (long)y * mapped.RowPitch, target + (long)y * _width * 4, _width * 4, _width * 4);
                }
                _context.Unmap(staging, 0);
                return pixels;
            }
            finally
            {
                Marshal.ReleaseComObject(staging);
            }
        }

        /// <summary>On a device of its own: for frames of the CPU's memory alone. The whole frame is shown.</summary>
        public D3D11FrameRenderer(VideoInfo info) : this(new Direct3DDevice(), info, 0, 0, true, ownsDevice: true)
        {
        }

        /// <summary>
        /// On the device frames are decoded on, which is what lets a frame of the GPU's memory be drawn from where it was
        /// decoded. The whole frame is shown.
        /// </summary>
        public D3D11FrameRenderer(Direct3DDevice device, VideoInfo info) : this(device, info, 0, 0, true, ownsDevice: false)
        {
        }

        /// <summary>
        /// As <see cref="D3D11FrameRenderer(Direct3DDevice, VideoInfo)"/>, but showing a view of the frame - one eye's
        /// picture, or a view into a spherical video - <paramref name="outputWidth"/> by <paramref name="outputHeight"/>.
        /// </summary>
        public D3D11FrameRenderer(Direct3DDevice device, VideoInfo info, uint outputWidth, uint outputHeight)
            : this(device, info, outputWidth, outputHeight, false, ownsDevice: false)
        {
        }

        private readonly Direct3DDevice _shared;
        private readonly bool _ownsDevice;

        private D3D11FrameRenderer(Direct3DDevice device, VideoInfo info, uint outputWidth, uint outputHeight, bool wholeFrame, bool ownsDevice)
        {
            _info = info ?? throw new ArgumentNullException(nameof(info));
            _shared = device ?? throw new ArgumentNullException(nameof(device));
            _ownsDevice = ownsDevice;
            // of the picture as it is shown: turned, as a frame as decoded is as it is converted
            _width = info.ShownWidth;
            _height = info.ShownHeight;
            IsWholeFrame = wholeFrame;
            _outputWidth = wholeFrame ? _width : Math.Max(1, outputWidth);
            _outputHeight = wholeFrame ? _height : Math.Max(1, outputHeight);
            _device = device.Device;
            _context = device.Context;

            // the frame converted, and drawn from; shared, where it is what is shown
            var pictureDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                MiscFlags = wholeFrame ? D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED : 0,
            };
            _device.CreateTexture2D(pictureDesc, null, out _picture);
            if (wholeFrame)
            {
                _output = _picture;
            }
            else
            {
                var outputDesc = pictureDesc;
                outputDesc.Width = _outputWidth;
                outputDesc.Height = _outputHeight;
                outputDesc.MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED;
                _device.CreateTexture2D(outputDesc, null, out _output);
            }
            HANDLE shared;
            ((IDXGIResource)_output).GetSharedHandle(&shared);
            SharedHandle = (IntPtr)shared.Value;

            _device.CreateQuery(new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT }, out _done);

            if (info.PixelFormat == PixelFormat.NV12 || info.PixelFormat == PixelFormat.Yuv)
                CreateYuvConverter();
            else if (info.PixelFormat == PixelFormat.BGR24)
                _bgra = new byte[_width * _height * 4];
        }

        /// <summary>
        /// The video processor, of frames decoded on the GPU - NV12 of a colour it shows as the shader would, of the matrix
        /// and the range it is told: see <see cref="VideoColour.IsVideoProcessorColour"/>.
        /// </summary>
        private void CreateVideoProcessor()
        {
            uint codedWidth = _info.Width, codedHeight = _info.Height;
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

            var outputViewDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
            {
                ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D,
            };
            _videoDevice.CreateVideoProcessorOutputView(_picture, _enumerator, outputViewDesc, out _outputView);

            // the picture is the top left of the coded frame; drawn over the whole of the output, turned clockwise as it is
            // shown - a mirrored one is not decoded on the GPU
            var source = new RECT(0, 0, (int)_info.OriginalWidth, (int)_info.OriginalHeight);
            var target = new RECT(0, 0, (int)_width, (int)_height);
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, &source);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, &target);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, &target);
            if (!_info.Orientation.IsIdentity)
            {
                var rotation = _info.Orientation.Degrees switch
                {
                    90 => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_90,
                    180 => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_180,
                    270 => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_270,
                    _ => D3D11_VIDEO_PROCESSOR_ROTATION.D3D11_VIDEO_PROCESSOR_ROTATION_IDENTITY,
                };
                _videoContext.VideoProcessorSetStreamRotation(_processor, 0, true, rotation);
            }

            // YCbCr in of the stream's matrix - BT.709's, or BT.601's - and range; full range RGB out. Its bits: Usage 0,
            // RGB_Range 1, YCbCr_Matrix 2, YCbCr_xvYCC 3, Nominal_Range 4 and 5 - of 16 to 235 1, of 0 to 255 2.
            var colour = _info.ColourOrDefault;
            var inputSpace = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE();
            inputSpace._bitfield = (colour.IsBt709Matrix ? 0b100u : 0u) | ((colour.FullRange ? 2u : 1u) << 4);
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, inputSpace);
            var outputSpace = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE();
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, outputSpace);

            _streams = new[] { new D3D11_VIDEO_PROCESSOR_STREAM { Enable = true } };
        }

        // the one stream converted, made once: an array a frame otherwise
        private D3D11_VIDEO_PROCESSOR_STREAM[] _streams;

        /// <summary>
        /// Draws a frame, of the format the source said, into the shared texture, and waits for the GPU to have done it: the
        /// other device reading it next does not wait for this one.
        /// </summary>
        public void Draw(object frame)
        {
            Convert(frame);
            WaitForGpu();
        }

        /// <summary>
        /// Draws a view of a frame into the shared texture, as <see cref="Draw(object)"/> does the whole frame: one eye's
        /// picture copied out of it, or a view into a spherical video drawn from it.
        /// </summary>
        public void Draw(object frame, in VideoView view)
        {
            Convert(frame);
            if (!IsWholeFrame)
            {
                if (view.Projection == VideoProjection.Equirectangular)
                    DrawSphere(view);
                else
                    CopyEye(view);
            }
            WaitForGpu();
        }

        /// <summary>
        /// Draws the view again from the frame drawn last, which is kept converted: as a paused view is turned. Of a renderer
        /// of a view only.
        /// </summary>
        public void Redraw(in VideoView view)
        {
            if (IsWholeFrame)
                return;
            if (view.Projection == VideoProjection.Equirectangular)
                DrawSphere(view);
            else
                CopyEye(view);
            WaitForGpu();
        }

        /// <summary>The frame, converted to BGRA, into the picture texture.</summary>
        private void Convert(object frame)
        {
            if (frame is GpuVideoFrame gpu)
                DrawTexture(gpu);
            else
                Draw((byte[])frame);
        }

        /// <summary>One eye's half of the frame into the output, which is its size: copied as it is.</summary>
        private void CopyEye(in VideoView view)
        {
            var (x, y, width, height) = view.EyeRect(_info, view.Eye == EyeView.Right);
            var box = new D3D11_BOX
            {
                left = x,
                top = y,
                right = x + Math.Min(width, _outputWidth),
                bottom = y + Math.Min(height, _outputHeight),
                front = 0,
                back = 1,
            };
            _context.CopySubresourceRegion(_output, 0, 0, 0, 0, _picture, 0, &box);
        }

        #region Spherical view

        // A triangle over the whole output, and a pixel of the view at each pixel of it: where it looks on the sphere, by the
        // yaw and the pitch, and where that is in the eye's equirectangular picture.
        private const string SphereShader = @"
cbuffer View : register(b0)
{
    float4 LeftEye;   // x, y, width, height of the left eye's picture, as fractions of the frame
    float4 RightEye;
    float4 Bounds;    // left, top, right, bottom of the whole sphere's picture cropped
    float4 Angles;    // yaw, pitch, tan of half the field of view, 1 for both eyes' views side by side
    float4 Output;    // the eye of a view of one, 1 the right; the output's width and height
    float4 Texel;     // the size of a pixel of the frame, as fractions of it
};
Texture2D Frame : register(t0);
SamplerState Linear : register(s0);

struct Vertex { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

Vertex VS(uint id : SV_VertexID)
{
    Vertex v;
    v.Uv = float2((id << 1) & 2, id & 2);
    v.Position = float4(v.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float4 PS(Vertex input) : SV_Target
{
    float2 uv = input.Uv;
    float right = Output.x;
    float width = Output.y;
    if (Angles.w > 0.5)
    {
        right = uv.x >= 0.5 ? 1 : 0;
        uv.x = frac(uv.x * 2);
        width *= 0.5;
    }
    float x = (uv.x * 2 - 1) * Angles.z;
    float y = (1 - uv.y * 2) * Angles.z * Output.z / width;

    // looking up by the pitch, then turned right by the yaw
    float cp = cos(Angles.y), sp = sin(Angles.y), cy = cos(Angles.x), sy = sin(Angles.x);
    float y1 = y * cp + sp;
    float z1 = -y * sp + cp;
    float x2 = x * cy + z1 * sy;
    float z2 = -x * sy + z1 * cy;
    float longitude = atan2(x2, z2);
    float latitude = atan2(y1, sqrt(x2 * x2 + z2 * z2));

    // of the whole sphere's picture, then of the part the eye's picture is of
    float u = (longitude / 6.28318530718 + 0.5 - Bounds.x) / (1 - Bounds.x - Bounds.z);
    float v = (0.5 - latitude / 3.14159265359 - Bounds.y) / (1 - Bounds.y - Bounds.w);
    if (u < 0 || u > 1 || v < 0 || v > 1)
        return float4(0, 0, 0, 1);

    // not across into the other eye's picture
    float4 eye = right > 0.5 ? RightEye : LeftEye;
    float2 p = clamp(eye.xy + float2(u, v) * eye.zw, eye.xy + Texel.xy * 0.5, eye.xy + eye.zw - Texel.xy * 0.5);
    return float4(Frame.SampleLevel(Linear, p, 0).rgb, 1);
}";

        [StructLayout(LayoutKind.Sequential)]
        private struct SphereConstants
        {
            public float LeftX, LeftY, LeftWidth, LeftHeight;
            public float RightX, RightY, RightWidth, RightHeight;
            public float BoundsLeft, BoundsTop, BoundsRight, BoundsBottom;
            public float Yaw, Pitch, TanHalf, BothEyes;
            public float Eye, OutputWidth, OutputHeight, Unused;
            public float TexelX, TexelY, Unused2, Unused3;
        }

        // compiled once, for every renderer
        private static byte[] _vertexShaderCode;
        private static byte[] _pixelShaderCode;
        private static readonly object _compileLock = new object();

        private ID3D11VertexShader _vertexShader;
        private ID3D11PixelShader _pixelShader;
        private ID3D11Buffer _constants;
        private ID3D11SamplerState _sampler;
        private ID3D11ShaderResourceView _pictureView;
        private ID3D11RenderTargetView _outputTarget;

        // what the context is given, made once: arrays a frame otherwise
        private ID3D11ShaderResourceView[] _views;
        private readonly ID3D11ShaderResourceView[] _noViews = new ID3D11ShaderResourceView[1];
        private ID3D11SamplerState[] _samplers;
        private ID3D11Buffer[] _buffers;
        private ID3D11RenderTargetView[] _targets;

        private void DrawSphere(in VideoView view)
        {
            if (_pixelShader == null)
                CreateSphereShader();

            var (lx, ly, lw, lh) = view.EyeRect(_info, false);
            var (rx, ry, rw, rh) = view.EyeRect(_info, true);
            float fw = _width, fh = _height;
            var constants = new SphereConstants
            {
                LeftX = lx / fw, LeftY = ly / fh, LeftWidth = lw / fw, LeftHeight = lh / fh,
                RightX = rx / fw, RightY = ry / fh, RightWidth = rw / fw, RightHeight = rh / fh,
                BoundsLeft = (float)view.Bounds.Left, BoundsTop = (float)view.Bounds.Top,
                BoundsRight = (float)view.Bounds.Right, BoundsBottom = (float)view.Bounds.Bottom,
                Yaw = (float)(view.Yaw * Math.PI / 180),
                Pitch = (float)(view.Pitch * Math.PI / 180),
                TanHalf = (float)Math.Tan(Math.Clamp(view.FieldOfView, 1, 179) * Math.PI / 360),
                BothEyes = view.IsBothSpherical ? 1 : 0,
                Eye = view.Eye == EyeView.Right && view.Layout != StereoLayout.Mono ? 1 : 0,
                OutputWidth = _outputWidth,
                OutputHeight = _outputHeight,
                TexelX = 1 / fw,
                TexelY = 1 / fh,
            };
            _context.UpdateSubresource(_constants, 0, null, &constants, 0, 0);

            var viewport = new D3D11_VIEWPORT { Width = _outputWidth, Height = _outputHeight, MaxDepth = 1 };
            _context.OMSetRenderTargets(1, _targets, null);
            _context.RSSetViewports(1, &viewport);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            _context.VSSetShader(_vertexShader, null, 0);
            _context.PSSetShader(_pixelShader, null, 0);
            _context.PSSetShaderResources(0, 1, _views);
            _context.PSSetSamplers(0, 1, _samplers);
            _context.PSSetConstantBuffers(0, 1, _buffers);
            _context.Draw(3, 0);

            // the picture is written by the video processor next: not bound to be read as well
            _context.PSSetShaderResources(0, 1, _noViews);
        }

        private void CreateSphereShader()
        {
            lock (_compileLock)
            {
                _vertexShaderCode ??= Compile(SphereShader, "SphereShader", "VS", "vs_4_0");
                _pixelShaderCode ??= Compile(SphereShader, "SphereShader", "PS", "ps_4_0");
            }

            fixed (byte* code = _vertexShaderCode)
            {
                ID3D11VertexShader_unmanaged* shader;
                _device.CreateVertexShader(code, (nuint)_vertexShaderCode.Length, null, &shader);
                _vertexShader = (ID3D11VertexShader)Wrap(shader);
            }
            fixed (byte* code = _pixelShaderCode)
            {
                ID3D11PixelShader_unmanaged* shader;
                _device.CreatePixelShader(code, (nuint)_pixelShaderCode.Length, null, &shader);
                _pixelShader = (ID3D11PixelShader)Wrap(shader);
            }

            var bufferDesc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(SphereConstants),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            };
            _device.CreateBuffer(bufferDesc, null, out _constants);

            var samplerDesc = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue,
            };
            _device.CreateSamplerState(samplerDesc, out _sampler);
            _device.CreateShaderResourceView((ID3D11Resource)_picture, null, out _pictureView);
            _device.CreateRenderTargetView((ID3D11Resource)_output, null, out _outputTarget);

            _views = new[] { _pictureView };
            _samplers = new[] { _sampler };
            _buffers = new[] { _constants };
            _targets = new[] { _outputTarget };
        }

        /// <summary>A wrapper of an object made, which the wrapper then holds alone.</summary>
        private static object Wrap(void* unknown)
        {
            var wrapper = Marshal.GetObjectForIUnknown((nint)unknown);
            Marshal.Release((nint)unknown);
            return wrapper;
        }

        private static byte[] Compile(string shader, string name, string entryPoint, string target)
        {
            var source = Encoding.ASCII.GetBytes(shader);
            var hr = PInvoke.D3DCompile(source, name, null, null, entryPoint, target, 0, 0, out ID3DBlob code, out ID3DBlob errors);
            if (hr.Failed)
            {
                string message = errors == null ? hr.ToString() : Marshal.PtrToStringAnsi((nint)errors.GetBufferPointer(), (int)errors.GetBufferSize());
                throw new InvalidOperationException($"The {name}'s {target} shader did not compile: {message}");
            }
            var bytes = new byte[(int)code.GetBufferSize()];
            new ReadOnlySpan<byte>(code.GetBufferPointer(), bytes.Length).CopyTo(bytes);
            Marshal.ReleaseComObject(code);
            return bytes;
        }

        #endregion

        /// <summary>
        /// A frame decoded on the GPU, converted where it is: through a view of its texture, made once for each texture of
        /// the pool, which is used again and again.
        /// </summary>
        private void DrawTexture(GpuVideoFrame frame)
        {
            if (_processor == null)
                CreateVideoProcessor();

            // the texture wrapped for this thread, the frame's being of the thread that decoded it: see Direct3DDevice
            if (!_inputViews.TryGetValue(frame.TexturePointer, out var input))
            {
                var texture = (ID3D11Texture2D)Direct3DDevice.ForThisThread(frame.TexturePointer);
                var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D };
                _videoDevice.CreateVideoProcessorInputView(texture, _enumerator, desc, out var view);
                input = (texture, view);
                _inputViews.Add(frame.TexturePointer, input);
            }
            _streams[0].pInputSurface = input.View;
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, _streams);
        }

        private readonly System.Collections.Generic.Dictionary<IntPtr, (ID3D11Texture2D Texture, ID3D11VideoProcessorInputView View)> _inputViews =
            new System.Collections.Generic.Dictionary<IntPtr, (ID3D11Texture2D Texture, ID3D11VideoProcessorInputView View)>();

        private void Draw(byte[] frame)
        {
            switch (_info.PixelFormat)
            {
                case PixelFormat.NV12:
                case PixelFormat.Yuv:
                    DrawYuv(frame);
                    break;

                case PixelFormat.BGRA32:
                    fixed (byte* data = frame)
                        _context.UpdateSubresource(_picture, 0, null, data, _width * 4, 0);
                    break;

                default:
                    WidenToBgra(frame);
                    fixed (byte* data = _bgra)
                        _context.UpdateSubresource(_picture, 0, null, data, _width * 4, 0);
                    break;
            }
        }

        /// <summary>
        /// Waits for the GPU to have drawn: the other device reading the texture next - Direct3D 9's, for WPF - does not wait
        /// for this one.
        /// </summary>
        private void WaitForGpu()
        {
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

        #region Frames as decoded

        // Of each pixel of the picture, its samples read of the frame's planes, as its format lays them out - each plane a
        // texture of the frame's bytes as they are - made into the three values of the stream's matrix and range, and those
        // shown through the table of the stream's colour: see VideoColour.
        private const string YuvShader = @"
cbuffer Colour : register(b0)
{
    float4 Coded;    // the layout: 0 4:2:0, 1 4:2:2 packed, 2 AYUV, 3 Y410 and Y416, 4 monochrome; the codes of a texture's 1, of the
                     // luma and of the chroma; 1 to look the values up in the table
    float4 Range;    // the luma's offset and scale, the chroma's
    float4 OfY;      // the three values' parts of the luma, of Cb below 0 and above, of Cr below 0 and above
    float4 CbBelow;
    float4 CbAbove;
    float4 CrBelow;
    float4 CrAbove;
    float4 Table;    // its steps each way
    float4 Turn;     // the matrix the picture is shown of, undone: of a pixel shown, where it is in the picture, A x + B y and C x + D y
    float4 TurnOffset;
};
Texture2D<float4> Plane0 : register(t0);
Texture2D<float4> Plane1 : register(t1);
Texture3D<float4> Lut : register(t2);
SamplerState Linear : register(s0);

struct Vertex { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };

Vertex VS(uint id : SV_VertexID)
{
    Vertex v;
    v.Uv = float2((id << 1) & 2, id & 2);
    v.Position = float4(v.Uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}

float4 PS(Vertex input) : SV_Target
{
    int2 shown = int2(input.Position.xy);
    int2 p = int2((int)Turn.x * shown.x + (int)Turn.y * shown.y + (int)TurnOffset.x, (int)Turn.z * shown.x + (int)Turn.w * shown.y + (int)TurnOffset.y);
    int layout = (int)Coded.x;
    float3 c;    // Y, Cb and Cr, each of a texture's 0 to 1
    bool monochrome = false;
    if (layout == 0)
    {
        c.x = Plane0.Load(int3(p, 0)).r;
        c.yz = Plane1.Load(int3(p / 2, 0)).rg;
    }
    else if (layout == 1)
    {
        // of each two pixels Y0, Cb, Y1 and Cr
        float4 q = Plane0.Load(int3(p.x / 2, p.y, 0));
        c = float3((p.x & 1) != 0 ? q.b : q.r, q.g, q.a);
    }
    else if (layout == 2)
    {
        // Cr, Cb, Y, alpha
        float4 q = Plane0.Load(int3(p, 0));
        c = float3(q.b, q.g, q.r);
    }
    else if (layout == 3)
    {
        // Cb, Y, Cr, alpha
        float4 q = Plane0.Load(int3(p, 0));
        c = float3(q.g, q.r, q.b);
    }
    else
    {
        c = float3(Plane0.Load(int3(p, 0)).r, 0, 0);
        monochrome = true;
    }

    float y = (c.x * Coded.y - Range.x) * Range.y;
    float cb = monochrome ? 0 : (c.y * Coded.z - Range.z) * Range.w;
    float cr = monochrome ? 0 : (c.z * Coded.z - Range.z) * Range.w;
    float3 t = OfY.xyz * y + (cb < 0 ? CbBelow.xyz : CbAbove.xyz) * cb + (cr < 0 ? CrBelow.xyz : CrAbove.xyz) * cr;
    if (Coded.w > 0.5)
    {
        // between the steps, of the steps' centres
        float n = Table.x;
        return float4(Lut.SampleLevel(Linear, saturate(t) * ((n - 1) / n) + 0.5 / n, 0).rgb, 1);
    }
    return float4(saturate(t), 1);
}";

        [StructLayout(LayoutKind.Sequential)]
        private struct YuvConstants
        {
            public float Layout, LumaCodes, ChromaCodes, LookUp;
            public float LumaOffset, LumaScale, ChromaOffset, ChromaScale;
            public float Y0, Y1, Y2, Unused0;
            public float CbBelow0, CbBelow1, CbBelow2, Unused1;
            public float CbAbove0, CbAbove1, CbAbove2, Unused2;
            public float CrBelow0, CrBelow1, CrBelow2, Unused3;
            public float CrAbove0, CrAbove1, CrAbove2, Unused4;
            public float TableSize, Unused5, Unused6, Unused7;
            public float TurnA, TurnB, TurnC, TurnD;
            public float TurnOffsetX, TurnOffsetY, Unused8, Unused9;
        }

        // compiled once, for every renderer
        private static byte[] _yuvVertexShaderCode;
        private static byte[] _yuvPixelShaderCode;

        private ID3D11VertexShader _yuvVertexShader;
        private ID3D11PixelShader _yuvPixelShader;
        private ID3D11Buffer _yuvConstants;
        private ID3D11SamplerState _tableSampler;
        private ID3D11Texture2D _plane0, _plane1;
        private ID3D11ShaderResourceView _plane0View, _plane1View;
        private ID3D11Texture3D _table;
        private ID3D11ShaderResourceView _tableView;
        private ID3D11RenderTargetView _pictureTarget;

        // the planes of the frame: their offsets, the bytes of a row, the rows; the second none where there is one
        private int _plane0Rows, _plane1Offset, _plane1Rows, _frameStride;

        // what the context is given, made once
        private ID3D11ShaderResourceView[] _yuvViews;
        private readonly ID3D11ShaderResourceView[] _noYuvViews = new ID3D11ShaderResourceView[3];
        private ID3D11SamplerState[] _yuvSamplers;
        private ID3D11Buffer[] _yuvBuffers;
        private ID3D11RenderTargetView[] _pictureTargets;
        private readonly ID3D11RenderTargetView[] _noTargets = new ID3D11RenderTargetView[1];

        /// <summary>
        /// The textures a frame as decoded is uploaded into - of its format's planes, each as the bytes lay them out - the
        /// shader that converts them, and the table of the stream's colour.
        /// </summary>
        private void CreateYuvConverter()
        {
            var format = _info.FrameFormat;
            var colour = _info.ColourOrDefault;
            var orientation = _info.Orientation;
            uint w = _info.Width, h = _info.Height;
            _frameStride = (int)VideoFrameFormats.Stride(format, w);
            _plane0Rows = (int)h;

            // the textures, and of a texture's 1 how many codes: of 16 bit samples, of the stream's bits in the high ones
            float codesOf16 = 65535f / (1 << (16 - colour.BitDepth));
            (DXGI_FORMAT Plane0, uint Width0, DXGI_FORMAT Plane1, int Layout, float Codes) = format switch
            {
                VideoFrameFormat.NV12 => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM, 0, 255f),
                VideoFrameFormat.P010 or VideoFrameFormat.P016 => (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM, 0, codesOf16),
                VideoFrameFormat.YUY2 => (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, (w + 1) / 2, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 1, 255f),
                VideoFrameFormat.Y210 or VideoFrameFormat.Y216 => (DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, (w + 1) / 2, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 1, codesOf16),
                VideoFrameFormat.AYUV => (DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 2, 255f),
                VideoFrameFormat.Y410 => (DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 3, 1023f),
                VideoFrameFormat.Y416 => (DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 3, codesOf16),
                VideoFrameFormat.L8 => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 4, 255f),
                VideoFrameFormat.L16 => (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, w, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 4, codesOf16),
                _ => throw new NotSupportedException($"Frames of {format} are not supported."),
            };

            _plane0 = CreatePlane(Plane0, Width0, h, out _plane0View);
            if (Plane1 != DXGI_FORMAT.DXGI_FORMAT_UNKNOWN)
            {
                // 4:2:0's chroma, after the luma's rows, of half the size each way
                _plane1Offset = _frameStride * (int)h;
                _plane1Rows = (int)(h + 1) / 2;
                _plane1 = CreatePlane(Plane1, (w + 1) / 2, (uint)_plane1Rows, out _plane1View);
            }

            var table = colour.Table;
            if (table != null)
            {
                const uint n = VideoColour.TableSize;
                var tableDesc = new D3D11_TEXTURE3D_DESC
                {
                    Width = n,
                    Height = n,
                    Depth = n,
                    MipLevels = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM,
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                };
                _device.CreateTexture3D(tableDesc, null, out _table);
                fixed (ushort* data = table)
                    _context.UpdateSubresource((ID3D11Resource)_table, 0, null, data, n * 8, n * n * 8);
                _device.CreateShaderResourceView((ID3D11Resource)_table, null, out _tableView);
            }

            lock (_compileLock)
            {
                _yuvVertexShaderCode ??= Compile(YuvShader, "YuvShader", "VS", "vs_4_0");
                _yuvPixelShaderCode ??= Compile(YuvShader, "YuvShader", "PS", "ps_4_0");
            }
            fixed (byte* code = _yuvVertexShaderCode)
            {
                ID3D11VertexShader_unmanaged* shader;
                _device.CreateVertexShader(code, (nuint)_yuvVertexShaderCode.Length, null, &shader);
                _yuvVertexShader = (ID3D11VertexShader)Wrap(shader);
            }
            fixed (byte* code = _yuvPixelShaderCode)
            {
                ID3D11PixelShader_unmanaged* shader;
                _device.CreatePixelShader(code, (nuint)_yuvPixelShaderCode.Length, null, &shader);
                _yuvPixelShader = (ID3D11PixelShader)Wrap(shader);
            }

            var constants = new YuvConstants
            {
                Layout = Layout,
                LumaCodes = Codes,
                ChromaCodes = Codes,
                LookUp = table != null ? 1 : 0,
                LumaOffset = colour.LumaOffset,
                LumaScale = colour.LumaScale,
                ChromaOffset = colour.ChromaOffset,
                ChromaScale = colour.ChromaScale,
                Y0 = colour.YCoefficients[0], Y1 = colour.YCoefficients[1], Y2 = colour.YCoefficients[2],
                CbBelow0 = colour.CbBelow[0], CbBelow1 = colour.CbBelow[1], CbBelow2 = colour.CbBelow[2],
                CbAbove0 = colour.CbAbove[0], CbAbove1 = colour.CbAbove[1], CbAbove2 = colour.CbAbove[2],
                CrBelow0 = colour.CrBelow[0], CrBelow1 = colour.CrBelow[1], CrBelow2 = colour.CrBelow[2],
                CrAbove0 = colour.CrAbove[0], CrAbove1 = colour.CrAbove[1], CrAbove2 = colour.CrAbove[2],
                TableSize = VideoColour.TableSize,
                TurnA = orientation.A, TurnB = orientation.B, TurnC = orientation.C, TurnD = orientation.D,
                TurnOffsetX = orientation.OffsetX((int)_info.OriginalWidth),
                TurnOffsetY = orientation.OffsetY((int)_info.OriginalHeight),
            };
            var bufferDesc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(YuvConstants),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            };
            _device.CreateBuffer(bufferDesc, null, out _yuvConstants);
            _context.UpdateSubresource(_yuvConstants, 0, null, &constants, 0, 0);

            var samplerDesc = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue,
            };
            _device.CreateSamplerState(samplerDesc, out _tableSampler);
            _device.CreateRenderTargetView((ID3D11Resource)_picture, null, out _pictureTarget);

            _yuvViews = new[] { _plane0View, _plane1View, _tableView };
            _yuvSamplers = new[] { _tableSampler };
            _yuvBuffers = new[] { _yuvConstants };
            _pictureTargets = new[] { _pictureTarget };
        }

        private ID3D11Texture2D CreatePlane(DXGI_FORMAT format, uint width, uint height, out ID3D11ShaderResourceView view)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            _device.CreateTexture2D(desc, null, out var texture);
            _device.CreateShaderResourceView((ID3D11Resource)texture, null, out view);
            return texture;
        }

        /// <summary>A frame as decoded: its planes uploaded as they are, and converted by the shader into the picture.</summary>
        private void DrawYuv(byte[] frame)
        {
            fixed (byte* data = frame)
            {
                _context.UpdateSubresource((ID3D11Resource)_plane0, 0, null, data, (uint)_frameStride, 0);
                if (_plane1 != null)
                {
                    // where the frame is shorter than its last chroma row - of an odd width - the rows it has
                    int rows = Math.Min(_plane1Rows, (frame.Length - _plane1Offset) / _frameStride);
                    var box = new D3D11_BOX { left = 0, top = 0, front = 0, right = (_info.Width + 1) / 2, bottom = (uint)rows, back = 1 };
                    _context.UpdateSubresource((ID3D11Resource)_plane1, 0, &box, data + _plane1Offset, (uint)_frameStride, 0);
                }
            }

            var viewport = new D3D11_VIEWPORT { Width = _width, Height = _height, MaxDepth = 1 };
            _context.OMSetRenderTargets(1, _pictureTargets, null);
            _context.RSSetViewports(1, &viewport);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            _context.VSSetShader(_yuvVertexShader, null, 0);
            _context.PSSetShader(_yuvPixelShader, null, 0);
            _context.PSSetShaderResources(0, 3, _yuvViews);
            _context.PSSetSamplers(0, 1, _yuvSamplers);
            _context.PSSetConstantBuffers(0, 1, _yuvBuffers);
            _context.Draw(3, 0);

            // the picture is read next, by the spherical view, or copied: not bound to be written as well
            _context.PSSetShaderResources(0, 3, _noYuvViews);
            _context.OMSetRenderTargets(1, _noTargets, null);
        }

        #endregion

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
            foreach (var input in _inputViews.Values)
            {
                Marshal.ReleaseComObject(input.View);
                Marshal.ReleaseComObject(input.Texture);
            }
            _inputViews.Clear();
            Release(ref _outputTarget);
            Release(ref _pictureView);
            Release(ref _sampler);
            Release(ref _constants);
            Release(ref _pixelShader);
            Release(ref _vertexShader);
            Release(ref _pictureTarget);
            Release(ref _tableSampler);
            Release(ref _yuvConstants);
            Release(ref _yuvPixelShader);
            Release(ref _yuvVertexShader);
            Release(ref _tableView);
            Release(ref _table);
            Release(ref _plane1View);
            Release(ref _plane1);
            Release(ref _plane0View);
            Release(ref _plane0);
            Release(ref _outputView);
            Release(ref _processor);
            Release(ref _enumerator);
            Release(ref _done);
            if (!ReferenceEquals(_output, _picture))
                Release(ref _output);
            _output = null;
            Release(ref _picture);
            _videoContext = null;
            _videoDevice = null;
            _context = null;
            _device = null;
            // the device is the source's too, unless it was made for this alone
            if (_ownsDevice)
                _shared.Dispose();
        }

        private static void Release<T>(ref T com) where T : class
        {
            if (com != null && Marshal.IsComObject(com))
                Marshal.ReleaseComObject(com);
            com = null;
        }
    }
}
