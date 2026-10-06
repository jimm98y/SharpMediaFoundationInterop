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
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Input
{
    [SupportedOSPlatform("windows10.0.17763.0")]
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

    [SupportedOSPlatform("windows10.0.17763.0")]
    public class ScreenCapture : IMediaVideoSource
    {
        private const uint BYTES_PER_PIXEL = 4;
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private IDXGIFactory1 _factory;
        private ID3D11Device3 _device;
        private ID3D11DeviceContext _context; // we need immediate context

        // The desktop's frames are copied, on the GPU, into these by turns, and each read on the call after: by then the copy
        // is done, and reading it does not wait for the GPU. Each holds the time of its frame, and whether it is still to be
        // read.
        private readonly ID3D11Texture2D[] _staging = new ID3D11Texture2D[2];
        private readonly long[] _stagingTime = new long[2];
        private readonly bool[] _stagingFull = new bool[2];
        private int _nextStaging;
        private IDXGIOutput _output;
        private IDXGIOutputDuplication _duplicatedOutput;
        private static readonly D3D_FEATURE_LEVEL[] _featureLevels = new[]
        {
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
            D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0
        };

        private bool _disposedValue;

        public uint OutputSize { get; private set; }
        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth { get { return Width; } }
        public uint OriginalHeight { get { return Height; } }

        public Guid OutputFormat { get; private set; } = PInvoke.MFVideoFormat_ARGB32;

        public uint ReadTimeoutInMilliseconds { get; set; } = 40;

        /// <summary>
        /// The order of rows <see cref="ReadSample(byte[], out long)"/> hands frames out in: bottom-up - the first row the
        /// bottom one - as Media Foundation's RGB formats are, unless set false, for top-down, as a bitmap is.
        /// </summary>
        public bool BottomUp { get; set; } = true;

        /// <summary>The screen <see cref="Initialize()"/> opens: the first adapter's first output, the primary, unless given.</summary>
        private readonly uint _adapterID, _outputID;

        public ScreenCapture()
        { }

        /// <summary>Of the screen <see cref="Initialize()"/> opens: an output of an adapter, as <see cref="Enumerate"/> gives them.</summary>
        public ScreenCapture(uint adapterID, uint outputID)
        {
            _adapterID = adapterID;
            _outputID = outputID;
        }

        public void Initialize()
        {
            Initialize(_adapterID, _outputID);
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
            D3D11_TEXTURE2D_DESC stagingDesc = new()
            {
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                SampleDesc = { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING
            };

            // must be set for the DuplicateOutput1 to succeed
            // in WPF app, this call requires [assembly: DisableDpiAwareness] attribute and app.manifest with Windows 10 compatibility
            PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            for (int i = 0; i < _staging.Length; i++)
                _device.CreateTexture2D(stagingDesc, null, out _staging[i]);

            // TODO https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu
            IDXGIOutputDuplication duplicatedOutput;
            output.DuplicateOutput1(_device, 0, new[] { DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM }, out duplicatedOutput);
            _duplicatedOutput = duplicatedOutput;

            _stopwatch.Start();
        }

        private IDXGIResource _screenResource;

        /// <summary>
        /// The next frame of the desktop, in the order of rows <see cref="BottomUp"/> says: see
        /// <see cref="ReadSample(byte[], bool, out long)"/>.
        /// </summary>
        public bool ReadSample(byte[] buffer, out long timestamp)
        {
            return ReadSample(buffer, BottomUp, out timestamp);
        }

        /// <summary>
        /// The next frame of the desktop that changed, into <paramref name="buffer"/>, of <see cref="OutputSize"/> bytes at
        /// least: copied once, straight from the GPU's copy of it, in the order of rows asked for - bottom-up for Media
        /// Foundation, top-down for a bitmap. Frames come a call late: each is read on the call after the one that took it,
        /// when the GPU has copied it, rather than waited for.
        /// </summary>
        /// <param name="timestamp">When the frame was taken, in 100 ns units since <see cref="Initialize()"/>.</param>
        /// <returns>False where there is no frame to hand out: the desktop has not changed, or the first is not yet copied.</returns>
        public unsafe bool ReadSample(byte[] buffer, bool bottomUp, out long timestamp)
        {
            if (_disposedValue)
                throw new ObjectDisposedException(nameof(ScreenCapture));
            if (buffer == null || buffer.Length < OutputSize)
                throw new ArgumentException($"A buffer of {OutputSize} bytes at least is needed.", nameof(buffer));

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
                        _screenResource = null;
                        _duplicatedOutput?.ReleaseFrame();
                    }
                    catch (Exception ex)
                    {
                        if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                    }
                }

                int written = -1;
                // the desktop not changed by the time out is no error: what is still to be read is handed out
                HRESULT acquired = _duplicatedOutput.AcquireNextFrame(ReadTimeoutInMilliseconds, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out _screenResource);
                if (acquired.Value == DXGI_ERROR_WAIT_TIMEOUT)
                    _screenResource = null;
                else
                    MediaUtils.Check(acquired);

                // a frame of the pointer alone has the desktop's image as it was: nothing to copy
                if (_screenResource != null && frameInfo.LastPresentTime != 0)
                {
                    written = _nextStaging;
                    _context.CopyResource(_staging[written], (ID3D11Texture2D)_screenResource);
                    // Media Foundation's time, 100 ns units, as every sample time is
                    _stagingTime[written] = _stopwatch.Elapsed.Ticks;
                    _stagingFull[written] = true;
                    _nextStaging ^= 1;
                }

                // the frame to hand out: one copied on a call before - the older, where both are - not the one copied now
                int older = _nextStaging, newer = _nextStaging ^ 1;
                int read = older != written && _stagingFull[older] ? older
                    : newer != written && _stagingFull[newer] ? newer
                    : -1;
                if (read < 0)
                    return false;

                D3D11_MAPPED_SUBRESOURCE mapped;
                _context.Map(_staging[read], 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                try
                {
                    uint rowBytes = Width * BYTES_PER_PIXEL;
                    fixed (byte* target = buffer)
                    {
                        for (uint y = 0; y < Height; y++)
                        {
                            byte* sourceRow = (byte*)mapped.pData + (long)y * mapped.RowPitch;
                            byte* targetRow = target + (long)(bottomUp ? Height - 1 - y : y) * rowBytes;
                            Buffer.MemoryCopy(sourceRow, targetRow, rowBytes, rowBytes);
                        }
                    }
                }
                finally
                {
                    _context.Unmap(_staging[read], 0);
                }

                _stagingFull[read] = false;
                timestamp = _stagingTime[read];
                return true;
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                return false;
            }
        }

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);

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
                    if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
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
                        if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
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
                    catch (Exception ex)
                    {
                        if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                    }
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

                for (int i = 0; i < _staging.Length; i++)
                {
                    if (_staging[i] != null)
                    {
                        Marshal.ReleaseComObject(_staging[i]);
                        _staging[i] = null;
                    }
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
