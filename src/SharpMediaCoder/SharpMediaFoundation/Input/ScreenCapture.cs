using System;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpMediaFoundation.Utils;
using SharpMediaFoundation.Transforms;

namespace SharpMediaFoundation.Input
{
    public class ScreenCapture : IMediaVideoSource
    {
        private const uint BYTES_PER_PIXEL = 4;
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private IDXGIFactory1 _factory;
        private ID3D11Device3 _device;
        private ID3D11DeviceContext _context;
        private ID3D11Texture2D _captureTexture;
        private IDXGIOutput _output;
        private IDXGIOutputDuplication _duplicatedOutput;

        private nint _pData;
        private bool _disposedValue;

        public uint OutputSize { get; private set; }
        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth { get { return Width; } }
        public uint OriginalHeight { get { return Height; } }

        public Guid OutputFormat { get; private set; } = PInvoke.MFVideoFormat_ARGB32;

        public unsafe void Initialize()
        {
            // TODO: reinitialize support when the device is lost
            PInvoke.CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factory);
            _factory = (IDXGIFactory1)factory;

            IDXGIAdapter adapter;
            _factory.EnumAdapters(0, out adapter);

            D3D_FEATURE_LEVEL level;
            ID3D11Device device;
            D3D_FEATURE_LEVEL[] featureLevels = new[]
            {
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
                D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0
            };

            fixed (D3D_FEATURE_LEVEL* pFeatureLevel = &featureLevels[0])
            {
                PInvoke.D3D11CreateDevice(
                    adapter,
                    D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                    (HMODULE)nint.Zero,
                    0,
                    pFeatureLevel,
                    (uint)featureLevels.Length,
                    PInvoke.D3D11_SDK_VERSION,
                    out device,
                    &level,
                    out var ctx);
            }
            _device = (ID3D11Device3)device;

            ID3D11DeviceContext deviceContext;
            _device.GetImmediateContext(out deviceContext);
            _context = deviceContext;

            IDXGIOutput outputEn;
            adapter.EnumOutputs(0, out outputEn);

            DXGI_OUTPUT_DESC outputDescription;
            outputEn.GetDesc(&outputDescription);
            _output = outputEn;

            // TODO: rotation support
            Width = (uint)outputDescription.DesktopCoordinates.Width;
            Height = (uint)outputDescription.DesktopCoordinates.Height;
            OutputSize = Width * Height * BYTES_PER_PIXEL;

            IDXGIOutput5 output = (IDXGIOutput5)outputEn;
            D3D11_TEXTURE2D_DESC captureTextureDesc = new()
            {
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Width = Width,
                Height = Height,
                MiscFlags = 0,
                MipLevels = 1,
                ArraySize = 1,
                SampleDesc = { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT
            };

            // must be set for the DuplicateOutput1 to succeed
            PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            ID3D11Texture2D captureTexture;
            _device.CreateTexture2D(&captureTextureDesc, null, out captureTexture);
            _captureTexture = captureTexture;

            // TODO https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu
            IDXGIOutputDuplication duplicatedOutput;
            output.DuplicateOutput1(_device, 0, new[] { DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM }, out duplicatedOutput);
            _duplicatedOutput = duplicatedOutput;

            _pData = Marshal.AllocHGlobal((int)(Width * Height * BYTES_PER_PIXEL));

            _stopwatch.Start();
        }

        public unsafe bool ReadSample(byte[] buffer, out long timestamp)
        {
            if (_disposedValue)
                throw new ObjectDisposedException(nameof(ScreenCapture));

            bool ret = false;
            IDXGIResource screenResource = default;
            timestamp = 0;

            try
            {
                _duplicatedOutput.AcquireNextFrame(0, out DXGI_OUTDUPL_FRAME_INFO duplicateFrameInformation, out screenResource);
                timestamp = _stopwatch.ElapsedTicks;

                if (screenResource != null)
                {
                    ID3D11Texture2D screenTexture = (ID3D11Texture2D)screenResource;
                    _context.CopyResource(_captureTexture, screenTexture);

                    const uint subresource = 0;
                    _context.Map(_captureTexture, subresource, D3D11_MAP.D3D11_MAP_READ, 0, null);

                    _device.ReadFromSubresource((void*)_pData, Width * BYTES_PER_PIXEL, Height, _captureTexture, 0);
                    BitmapUtils.CopyBitmap(
                            _pData,
                            (int)Width,
                            (int)Height,
                            buffer,
                            (int)Width,
                            (int)Height,
                            (int)BYTES_PER_PIXEL,
                            true);

                    _context.Unmap(_captureTexture, subresource);

                    ret = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                if (screenResource != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(screenResource);
                        _duplicatedOutput?.ReleaseFrame();
                    }
                    catch (Exception eex)
                    {
                        Debug.WriteLine(eex.Message);
                    }
                }
            }

            return ret;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                _disposedValue = true;

                if (_factory != null)
                {
                    Marshal.ReleaseComObject(_factory);
                    _factory = null;
                }

                if (_device != null)
                {
                    Marshal.ReleaseComObject(_device);
                    _device = null;
                }

                if (_context != null)
                {
                    Marshal.ReleaseComObject(_context);
                    _context = null;
                }

                if (_captureTexture != null)
                {
                    Marshal.ReleaseComObject(_captureTexture);
                    _captureTexture = null;
                }

                if (_output != null)
                {
                    Marshal.ReleaseComObject(_output);
                    _output = null;
                }

                if (_duplicatedOutput != null)
                {
                    Marshal.ReleaseComObject(_duplicatedOutput);
                    _duplicatedOutput = null;
                }

                if (_pData != nint.Zero)
                {
                    Marshal.FreeHGlobal(_pData);
                    _pData = nint.Zero;
                }
            }
        }

        ~ScreenCapture()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
