using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A Direct3D 11 device for video: what a decoder decodes on, a source copies its frames on and a control draws them
    /// with - one device, so that a frame decoded is drawn without leaving the GPU.
    /// </summary>
    /// <remarks>
    /// Used on the video's thread and the UI's, which are of different COM apartments: a wrapper of a COM object is of the
    /// apartment it was made in, and Direct3D's objects cannot be marshaled to another - a call from the other fails as
    /// unsupported. Direct3D 11 is free threaded itself, and protected here for more than one thread at a time, so each
    /// thread gets a wrapper of its own of the one object.
    /// </remarks>
    public sealed class Direct3DDevice : IDisposable
    {
        private IntPtr _device;
        private IntPtr _context;
        private IntPtr _nativeDevice;

        private readonly ThreadLocal<ID3D11Device> _threadDevice;
        private readonly ThreadLocal<ID3D11DeviceContext> _threadContext;

        /// <summary>The device, wrapped for the calling thread.</summary>
        internal ID3D11Device Device => _threadDevice.Value;

        /// <summary>Its immediate context, wrapped for the calling thread.</summary>
        internal ID3D11DeviceContext Context => _threadContext.Value;

        /// <summary>
        /// The device as a decoder is given it, an <c>ID3D11Device</c> pointer: see
        /// <see cref="SharpMediaFoundationInterop.Codecs.IGpuVideoTransform.GpuDevice"/>.
        /// </summary>
        internal IntPtr NativeDevice => _nativeDevice;

        public Direct3DDevice()
        {
            var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                null, PInvoke.D3D11_SDK_VERSION, out ID3D11Device device, out _, out ID3D11DeviceContext context);
            Marshal.ThrowExceptionForHR(hr);

            // the decoder's thread, the source's and the UI's use it: one call at a time into it
            ((ID3D10Multithread)device).SetMultithreadProtected(true);


            _device = Marshal.GetIUnknownForObject(device);
            _context = Marshal.GetIUnknownForObject(context);
            _nativeDevice = Marshal.GetComInterfaceForObject(device, typeof(ID3D11Device));

            // this thread's are the ones it was made with
            _threadDevice = new ThreadLocal<ID3D11Device>(() => (ID3D11Device)ForThisThread(_device));
            _threadContext = new ThreadLocal<ID3D11DeviceContext>(() => (ID3D11DeviceContext)ForThisThread(_context));
            _threadDevice.Value = device;
            _threadContext.Value = context;
        }

        /// <summary>A wrapper of the object, of the calling thread's apartment, whichever thread made the object.</summary>
        internal static object ForThisThread(IntPtr unknown)
        {
            return Marshal.GetUniqueObjectForIUnknown(unknown);
        }

        /// <summary>
        /// Lets go of the device. The wrappers each thread made are let go of as they are collected: released from another
        /// thread than their own, they would be called back on it.
        /// </summary>
        public void Dispose()
        {
            Release(ref _nativeDevice);
            Release(ref _context);
            Release(ref _device);
        }

        private static void Release(ref IntPtr unknown)
        {
            if (unknown != IntPtr.Zero)
                Marshal.Release(unknown);
            unknown = IntPtr.Zero;
        }
    }
}
