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
using SharpMediaFoundationInterop.Utils;
using SharpMediaFoundationInterop.Transforms;
using System.Collections.Generic;

namespace SharpMediaFoundationInterop.Input
{
    public class ScreenDevice
    {
        public uint AdapterID { get; private set; }
        public uint OutputID { get; private set; }

        public int X { get; private set; }
        public int Y { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string DeviceName { get; private set; }
        public int Rotation { get; private set; }

        public ScreenDevice(uint adapterID, uint outputID, int x, int y, int width, int height, int rotation, string deviceName)
        {
            this.AdapterID = adapterID;
            this.OutputID = outputID;

            this.X = x;
            this.Y = y;
            this.Width = width;
            this.Height = height;
            this.Rotation = rotation;
            this.DeviceName = deviceName;
        }
    }

    public class ScreenCapture : IMediaVideoSource
    {
        private const uint BYTES_PER_PIXEL = 4;
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private IDXGIFactory1 _factory;
        private ID3D11Device3 _device;
        private ID3D11DeviceContext _context; // we need immediate context
        private ID3D11Texture2D _captureTexture;
        private IDXGIOutput _output;
        private IDXGIOutputDuplication _duplicatedOutput;
        private static readonly D3D_FEATURE_LEVEL[] _featureLevels = new[]
        {
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0
        };

        private nint _pData;
        private bool _disposedValue;

        public uint OutputSize { get; private set; }
        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth { get { return Width; } }
        public uint OriginalHeight { get { return Height; } }

        public Guid OutputFormat { get; private set; } = PInvoke.MFVideoFormat_ARGB32;

        public uint ReadTimeoutInMilliseconds { get; set; } = 40;

        public void Initialize()
        {
            Initialize(0, 0);
        }

        public void Initialize(ScreenDevice device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            Initialize(device.AdapterID, device.OutputID);
        }

        public unsafe void Initialize(uint adapterID, uint outputID)
        {
            // TODO: reinitialize support when the device is lost
            PInvoke.CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factory);
            _factory = (IDXGIFactory1)factory;

            IDXGIAdapter adapter;
            _factory.EnumAdapters(adapterID, out adapter); // first returns the adapter with the output on which the desktop primary is displayed 

            D3D_FEATURE_LEVEL level;
            ID3D11Device device;

            fixed (D3D_FEATURE_LEVEL* pFeatureLevel = &_featureLevels[0])
            {
                PInvoke.D3D11CreateDevice(
                    adapter,
                    D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                    (HMODULE)nint.Zero,
                    0,
                    pFeatureLevel,
                    (uint)_featureLevels.Length,
                    PInvoke.D3D11_SDK_VERSION,
                    out device,
                    &level,
                    out ID3D11DeviceContext deviceContext);
                _context = deviceContext;
            }
            _device = (ID3D11Device3)device;

            IDXGIOutput outputEn;
            adapter.EnumOutputs(outputID, out outputEn);

            DXGI_OUTPUT_DESC outputDescription = outputEn.GetDesc();
            _output = outputEn;

            // TODO: rotation support
            Width = (uint)outputDescription.DesktopCoordinates.Width;
            Height = (uint)outputDescription.DesktopCoordinates.Height;
            OutputSize = Width * Height * BYTES_PER_PIXEL;

            IDXGIOutput5 output = (IDXGIOutput5)outputEn;
            D3D11_TEXTURE2D_DESC captureTextureDesc = new()
            {
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Width = Width,
                Height = Height,
                MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED,
                MipLevels = 1,
                ArraySize = 1,
                SampleDesc = { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT
            };

            // must be set for the DuplicateOutput1 to succeed
            // in WPF app, this call requires [assembly: DisableDpiAwareness] attribute and app.manifest with Windows 10 compatibility
            PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            ID3D11Texture2D_unmanaged* captureTexture;
            _device.CreateTexture2D(&captureTextureDesc, null, &captureTexture);
            _captureTexture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)captureTexture);

            // TODO https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu
            IDXGIOutputDuplication duplicatedOutput;
            output.DuplicateOutput1(_device, 0, new[] { DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM }, out duplicatedOutput);
            _duplicatedOutput = duplicatedOutput;

            _pData = Marshal.AllocHGlobal((int)(Width * Height * BYTES_PER_PIXEL));

            _stopwatch.Start();
        }

        private IDXGIResource _screenResource;

        public unsafe bool ReadSample(byte[] buffer, out long timestamp)
        {
            if (_disposedValue)
                throw new ObjectDisposedException(nameof(ScreenCapture));

            bool ret = false;
            timestamp = 0;

            try
            {
                if (_screenResource != null)
                {
                    /*
                    For performance reasons, we recommend that you release the frame just before you call the IDXGIOutputDuplication::AcquireNextFrame 
                    method to acquire the next frame. When the client does not own the frame, the operating system copies all desktop updates to the surface.
                    This can result in wasted GPU cycles if the operating system updates the same region for each frame that occurs.
                     */
                    try
                    {
                        Marshal.ReleaseComObject(_screenResource);
                        _duplicatedOutput?.ReleaseFrame();
                    }
                    catch (Exception eex)
                    {
                        if (Log.ErrorEnabled) Log.Error(eex.Message);
                    }
                }

                _duplicatedOutput.AcquireNextFrame(ReadTimeoutInMilliseconds, out DXGI_OUTDUPL_FRAME_INFO duplicateFrameInformation, out _screenResource);
                timestamp = _stopwatch.ElapsedMilliseconds * 10L;

                if (_screenResource != null)
                {
                    ID3D11Texture2D screenTexture = (ID3D11Texture2D)_screenResource;
                    _context.CopyResource(_captureTexture, screenTexture);

                    const uint subresource = 0;
                    ((ID3D11DeviceContext3)_context).Map(_captureTexture, subresource, D3D11_MAP.D3D11_MAP_READ, 0, null); 

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
                if (Log.ErrorEnabled) Log.Error(ex.Message);
            }

            return ret;
        }

        public static unsafe ScreenDevice[] Enumerate()
        {
            List<ScreenDevice> ret = new List<ScreenDevice>();

            PInvoke.CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factory);
            var factory1 = (IDXGIFactory1)factory;

            uint adapterID = 0;
            while (true)
            {
                IDXGIAdapter adapter;
                try
                {
                    factory1.EnumAdapters(adapterID, out adapter); // first returns the adapter with the output on which the desktop primary is displayed 
                }
                catch(Exception ex)
                {
                    if (Log.ErrorEnabled) Log.Error(ex.Message);
                    break;
                }

                D3D_FEATURE_LEVEL level;
                ID3D11Device device;

                fixed (D3D_FEATURE_LEVEL* pFeatureLevel = &_featureLevels[0])
                {
                    PInvoke.D3D11CreateDevice(
                        adapter,
                        D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                        (HMODULE)nint.Zero,
                        0,
                        pFeatureLevel,
                        (uint)_featureLevels.Length,
                        PInvoke.D3D11_SDK_VERSION,
                        out device,
                        &level,
                        out _);
                }
                var device3 = (ID3D11Device3)device;

                uint outputID = 0;
                while (true)
                {
                    IDXGIOutput outputEn;

                    try
                    {
                        MediaUtils.Check(adapter.EnumOutputs(outputID, out outputEn));
                    }
                    catch (Exception ex)
                    {
                        if (Log.ErrorEnabled) Log.Error(ex.Message);
                        break;
                    }

                    DXGI_OUTPUT_DESC outputDescription = outputEn.GetDesc();

                    var screenDevice = new ScreenDevice(
                            adapterID,
                            outputID,
                            outputDescription.DesktopCoordinates.X,
                            outputDescription.DesktopCoordinates.Y,
                            outputDescription.DesktopCoordinates.Width,
                            outputDescription.DesktopCoordinates.Height,
                            (int)outputDescription.Rotation,
                            outputDescription.DeviceName.ToString());
                    ret.Add(screenDevice);

                    outputID++;

                    Marshal.ReleaseComObject(outputEn);
                    outputEn = null;
                }

                if (device != null)
                {
                    Marshal.ReleaseComObject(device);
                    device = null;
                }

                adapterID++;
            }

            Marshal.ReleaseComObject(factory1);
            factory1 = null;

            return ret.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                _disposedValue = true;

                var screenResource = _screenResource;
                if (screenResource != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(screenResource);
                    }
                    catch (Exception eex)
                    { }
                }

                if (_factory != null)
                {
                    Marshal.ReleaseComObject(_factory);
                    _factory = null;
                }

                if (_duplicatedOutput != null)
                {
                    _duplicatedOutput.ReleaseFrame();
                    Marshal.ReleaseComObject(_duplicatedOutput);
                    _duplicatedOutput = null;
                }

                if (_captureTexture != null)
                {
                    Marshal.ReleaseComObject(_captureTexture);
                    _captureTexture = null;
                }

                if (_context != null)
                {
                    Marshal.ReleaseComObject(_context);
                    _context = null;
                }

                if (_output != null)
                {
                    Marshal.ReleaseComObject(_output);
                    _output = null;
                }

                if (_device != null)
                {
                    Marshal.ReleaseComObject(_device);
                    _device = null;
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
