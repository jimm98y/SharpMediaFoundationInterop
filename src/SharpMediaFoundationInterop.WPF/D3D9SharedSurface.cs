using System;
using System.Runtime.InteropServices;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A Direct3D 9Ex surface of a texture shared from Direct3D 11: what WPF's D3DImage takes. Direct3D 9 is called through
    /// the few methods of it this needs, by their place in its interfaces' tables: its bindings are generated only for one
    /// CPU architecture at a time, and this library is built for any.
    /// </summary>
    internal sealed unsafe class D3D9SharedSurface : IDisposable
    {
        private const uint D3D_SDK_VERSION = 32;
        private const int D3DDEVTYPE_HAL = 1;
        private const uint D3DCREATE_FPU_PRESERVE = 0x2;
        private const uint D3DCREATE_MULTITHREADED = 0x4;
        private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;
        private const int D3DSWAPEFFECT_DISCARD = 1;
        private const uint D3DPRESENT_INTERVAL_IMMEDIATE = 0x80000000;
        private const uint D3DUSAGE_RENDERTARGET = 1;
        private const int D3DFMT_A8R8G8B8 = 21;
        private const int D3DPOOL_DEFAULT = 0;

        // the methods' places in the tables of IDirect3D9Ex, IDirect3DDevice9Ex and IDirect3DTexture9
        private const int CreateDeviceExSlot = 20;
        private const int CreateTextureSlot = 23;
        private const int GetSurfaceLevelSlot = 18;

        /// <summary>Laid out as its fields fall, the window's handle at 32 bytes in on 64 bits: packed to 4 it is refused.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct D3DPRESENT_PARAMETERS
        {
            public uint BackBufferWidth;
            public uint BackBufferHeight;
            public int BackBufferFormat;
            public uint BackBufferCount;
            public int MultiSampleType;
            public uint MultiSampleQuality;
            public int SwapEffect;
            public IntPtr hDeviceWindow;
            public int Windowed;
            public int EnableAutoDepthStencil;
            public int AutoDepthStencilFormat;
            public uint Flags;
            public uint FullScreen_RefreshRateInHz;
            public uint PresentationInterval;
        }

        [DllImport("d3d9.dll")]
        private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr d3d);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private IntPtr _d3d;
        private IntPtr _device;
        private IntPtr _texture;
        private IntPtr _surface;

        /// <summary>The IDirect3DSurface9 to give D3DImage.</summary>
        public IntPtr Surface => _surface;

        /// <summary>Opens the shared texture, of this size, on a device of its own.</summary>
        public D3D9SharedSurface(IntPtr sharedHandle, uint width, uint height)
        {
            try
            {
                Marshal.ThrowExceptionForHR(Direct3DCreate9Ex(D3D_SDK_VERSION, out _d3d));

                // windowed, and presenting nothing: the device is for the texture; WPF presents
                var parameters = new D3DPRESENT_PARAMETERS
                {
                    BackBufferWidth = 1,
                    BackBufferHeight = 1,
                    SwapEffect = D3DSWAPEFFECT_DISCARD,
                    hDeviceWindow = GetDesktopWindow(),
                    Windowed = 1,
                    PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE,
                };
                IntPtr device;
                var createDeviceEx = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, D3DPRESENT_PARAMETERS*, void*, IntPtr*, int>)Slot(_d3d, CreateDeviceExSlot);
                Marshal.ThrowExceptionForHR(createDeviceEx(_d3d, 0, D3DDEVTYPE_HAL, parameters.hDeviceWindow,
                    D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE, &parameters, null, &device));
                _device = device;

                // the texture Direct3D 11 made, opened by its handle: BGRA there is A8R8G8B8 here
                IntPtr texture;
                IntPtr handle = sharedHandle;
                var createTexture = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Slot(_device, CreateTextureSlot);
                Marshal.ThrowExceptionForHR(createTexture(_device, width, height, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &texture, &handle));
                _texture = texture;

                IntPtr surface;
                var getSurfaceLevel = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(_texture, GetSurfaceLevelSlot);
                Marshal.ThrowExceptionForHR(getSurfaceLevel(_texture, 0, &surface));
                _surface = surface;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static IntPtr Slot(IntPtr com, int slot) => ((IntPtr*)*(IntPtr*)com)[slot];

        public void Dispose()
        {
            Release(ref _surface);
            Release(ref _texture);
            Release(ref _device);
            Release(ref _d3d);
        }

        private static void Release(ref IntPtr com)
        {
            if (com != IntPtr.Zero)
                Marshal.Release(com);
            com = IntPtr.Zero;
        }
    }
}
