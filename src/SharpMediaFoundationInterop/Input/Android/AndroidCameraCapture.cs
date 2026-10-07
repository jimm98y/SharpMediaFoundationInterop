using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.CameraNdk;
using static SharpMediaFoundationInterop.Utils.MediaNdk;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Captures a camera on Android, of the NDK's Camera2: a repeating request of the camera's widest YUV size into an
    /// AImageReader, its frames handed out as NV12. The app must hold the CAMERA permission, granted by the user - which
    /// only its Java side can ask for; <see cref="Initialize"/> throws <see cref="UnauthorizedAccessException"/> where it
    /// has none.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    public sealed unsafe class AndroidCameraCapture : IMediaVideoSource
    {
        /// <summary>How long <see cref="ReadSample"/> waits for a frame before it gives up and returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 2000;

        private readonly string _cameraId;
        private IntPtr _manager, _device, _reader, _window, _output, _container, _target, _request, _session;
        private DeviceStateCallbacks* _deviceCallbacks;
        private SessionStateCallbacks* _sessionCallbacks;
        private long _firstTime = -1;
        private bool _disposed;

        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth => Width;
        public uint OriginalHeight => Height;
        public Guid OutputFormat => MediaFormats.NV12;
        public uint OutputSize => Width * Height * 3 / 2;

        /// <param name="cameraId">The camera's id, as <see cref="MediaDevices.GetCameras"/> gives it; null for the first.</param>
        public AndroidCameraCapture(string cameraId = null)
        {
            _cameraId = cameraId;
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session != IntPtr.Zero)
                return;

            string id = _cameraId ?? (CameraNdk.List() is { Length: > 0 } cameras ? cameras[0].Id : throw new InvalidOperationException("There is no camera"));
            _manager = ACameraManager_create();
            var (width, height) = LargestYuvSize(_manager, id);
            if (width == 0)
                throw new InvalidOperationException($"The camera {id} has no YUV output");
            Width = (uint)width;
            Height = (uint)height;

            // the callbacks are kept in native memory: Camera2 holds the pointers to them as long as the camera is open
            _deviceCallbacks = (DeviceStateCallbacks*)NativeMemory.AllocZeroed((nuint)sizeof(DeviceStateCallbacks));
            _deviceCallbacks->OnDisconnected = &OnDisconnected;
            _deviceCallbacks->OnError = &OnError;
            int status = ACameraManager_openCamera(_manager, id, _deviceCallbacks, out _device);
            if (status == ACAMERA_ERROR_PERMISSION_DENIED)
                throw new UnauthorizedAccessException("The app may not use the camera: it needs the CAMERA permission, granted");
            Check(status, "open the camera");

            Check(AImageReader_new(width, height, AIMAGE_FORMAT_YUV_420_888, 4, out _reader), "make an image reader");
            Check(AImageReader_getWindow(_reader, out _window), "get the image reader's window");
            Check(ACaptureSessionOutput_create(_window, out _output), "make the session's output");
            Check(ACaptureSessionOutputContainer_create(out _container), "make the session's outputs");
            Check(ACaptureSessionOutputContainer_add(_container, _output), "add the session's output");
            Check(ACameraOutputTarget_create(_window, out _target), "make the request's target");
            Check(ACameraDevice_createCaptureRequest(_device, TEMPLATE_RECORD, out _request), "make the capture request");
            Check(ACaptureRequest_addTarget(_request, _target), "add the request's target");

            _sessionCallbacks = (SessionStateCallbacks*)NativeMemory.AllocZeroed((nuint)sizeof(SessionStateCallbacks));
            _sessionCallbacks->OnClosed = &OnSessionState;
            _sessionCallbacks->OnReady = &OnSessionState;
            _sessionCallbacks->OnActive = &OnSessionState;
            Check(ACameraDevice_createCaptureSession(_device, _container, _sessionCallbacks, out _session), "make the capture session");
            IntPtr request = _request;
            Check(ACameraCaptureSession_setRepeatingRequest(_session, IntPtr.Zero, 1, &request, IntPtr.Zero), "start capturing");
        }

        private static void Check(int status, string what)
        {
            if (status != ACAMERA_OK)
                throw new InvalidOperationException($"Could not {what}: {status}");
        }

        /// <summary>
        /// The newest frame, NV12, of <see cref="Width"/> by <see cref="Height"/>, and its time, from the first frame's:
        /// waiting for one up to <see cref="ReadTimeoutInMilliseconds"/>.
        /// </summary>
        public bool ReadSample(byte[] sampleBytes, out long timestamp)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            timestamp = 0;
            if (_session == IntPtr.Zero)
                throw new InvalidOperationException("The capture is to be initialized first.");

            var stopwatch = Stopwatch.StartNew();
            IntPtr image;
            while (AImageReader_acquireLatestImage(_reader, out image) != AMEDIA_OK)
            {
                if (stopwatch.ElapsedMilliseconds > ReadTimeoutInMilliseconds)
                    return false;
                Thread.Sleep(2);
            }
            try
            {
                AImage_getTimestamp(image, out long ns);
                long time = ns / 100;
                if (_firstTime < 0)
                    _firstTime = time;
                timestamp = time - _firstTime;
                return CopyNV12(image, sampleBytes, Width, Height, out _, out _);
            }
            finally
            {
                AImage_delete(image);
            }
        }

        [UnmanagedCallersOnly]
        private static void OnDisconnected(IntPtr context, IntPtr device)
        {
            if (Log.WarnEnabled)
                Log.Warn("The camera was disconnected");
        }

        [UnmanagedCallersOnly]
        private static void OnError(IntPtr context, IntPtr device, int error)
        {
            if (Log.ErrorEnabled)
                Log.Error($"The camera failed: {error}");
        }

        [UnmanagedCallersOnly]
        private static void OnSessionState(IntPtr context, IntPtr session)
        {
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_session != IntPtr.Zero)
            {
                ACameraCaptureSession_stopRepeating(_session);
                ACameraCaptureSession_close(_session);
            }
            if (_request != IntPtr.Zero)
                ACaptureRequest_free(_request);
            if (_target != IntPtr.Zero)
                ACameraOutputTarget_free(_target);
            if (_container != IntPtr.Zero)
                ACaptureSessionOutputContainer_free(_container);
            if (_output != IntPtr.Zero)
                ACaptureSessionOutput_free(_output);
            if (_device != IntPtr.Zero)
                ACameraDevice_close(_device);
            if (_reader != IntPtr.Zero)
                AImageReader_delete(_reader);
            if (_manager != IntPtr.Zero)
                ACameraManager_delete(_manager);
            NativeMemory.Free(_deviceCallbacks);
            NativeMemory.Free(_sessionCallbacks);
            _session = IntPtr.Zero;
        }
    }
}
