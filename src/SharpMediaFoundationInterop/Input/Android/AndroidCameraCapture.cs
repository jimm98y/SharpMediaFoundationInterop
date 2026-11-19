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
    /// has none. Its zoom, exposure and focus are Camera2's, of a camera that has them: no phone's camera pans or tilts.
    /// </summary>
    [SupportedOSPlatform("android26.0")]
    internal sealed unsafe class AndroidCameraCapture : IMediaVideoSource, ICameraControl
    {
        /// <summary>How long <see cref="ReadSample"/> waits for a frame before it gives up and returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 2000;

        private readonly string _cameraId;
        private IntPtr _manager, _device, _reader, _window, _output, _container, _target, _request, _session, _characteristics;
        private readonly object _requestLock = new object();
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
            _characteristics = Characteristics(_manager, id);
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

            long start = Stopwatch.GetTimestamp();
            IntPtr image;
            while (AImageReader_acquireLatestImage(_reader, out image) != AMEDIA_OK)
            {
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > ReadTimeoutInMilliseconds)
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

        public bool TryGetRange(CameraProperty property, out int min, out int max, out int step, out int defaultValue)
        {
            (min, max, step, defaultValue) = (0, 0, 1, 0);
            switch (property)
            {
                case CameraProperty.Zoom:
                    // of Android 11: the ratio, times 100
                    var zoom = Entry<float>(_characteristics, ACAMERA_CONTROL_ZOOM_RATIO_RANGE);
                    if (zoom.Length < 2)
                        return false;
                    (min, max, defaultValue) = ((int)Math.Round(zoom[0] * 100), (int)Math.Round(zoom[1] * 100), 100);
                    return true;
                case CameraProperty.Exposure:
                    // of a camera whose exposure can be set by hand: in microseconds
                    var times = Entry<long>(_characteristics, ACAMERA_SENSOR_INFO_EXPOSURE_TIME_RANGE);
                    if (times.Length < 2 || Entry<byte>(_characteristics, ACAMERA_CONTROL_AE_AVAILABLE_MODES).IndexOf(ACAMERA_CONTROL_AE_MODE_OFF) < 0)
                        return false;
                    min = (int)Math.Max(1, times[0] / 1000);
                    max = (int)Math.Min(int.MaxValue, times[1] / 1000);
                    defaultValue = Math.Clamp(1000000 / 60, min, max);
                    return true;
                case CameraProperty.Focus:
                    // of a lens that moves: diopters, times 100, of 0 - infinity - to the nearest it focuses
                    var nearest = Entry<float>(_characteristics, ACAMERA_LENS_INFO_MINIMUM_FOCUS_DISTANCE);
                    if (nearest.Length < 1 || nearest[0] <= 0 || Entry<byte>(_characteristics, ACAMERA_CONTROL_AF_AVAILABLE_MODES).IndexOf(ACAMERA_CONTROL_AF_MODE_OFF) < 0)
                        return false;
                    max = (int)Math.Round(nearest[0] * 100);
                    return true;
                default:
                    return false;
            }
        }

        public bool TryGet(CameraProperty property, out int value, out bool auto)
        {
            (value, auto) = (0, false);
            if (!TryGetRange(property, out _, out _, out _, out int defaultValue))
                return false;
            lock (_requestLock)
            {
                if (_request == IntPtr.Zero)
                    return false;
                switch (property)
                {
                    case CameraProperty.Zoom:
                        var zoom = RequestEntry<float>(_request, ACAMERA_CONTROL_ZOOM_RATIO);
                        value = zoom.Length > 0 ? (int)Math.Round(zoom[0] * 100) : defaultValue;
                        return true;
                    case CameraProperty.Exposure:
                        // of auto exposure, the time chosen is of each frame's result, not of the request: the last set
                        var ae = RequestEntry<byte>(_request, ACAMERA_CONTROL_AE_MODE);
                        auto = ae.Length == 0 || ae[0] != ACAMERA_CONTROL_AE_MODE_OFF;
                        var time = RequestEntry<long>(_request, ACAMERA_SENSOR_EXPOSURE_TIME);
                        value = time.Length > 0 ? (int)Math.Min(int.MaxValue, time[0] / 1000) : defaultValue;
                        return true;
                    case CameraProperty.Focus:
                        var af = RequestEntry<byte>(_request, ACAMERA_CONTROL_AF_MODE);
                        auto = af.Length == 0 || af[0] != ACAMERA_CONTROL_AF_MODE_OFF;
                        var distance = RequestEntry<float>(_request, ACAMERA_LENS_FOCUS_DISTANCE);
                        value = distance.Length > 0 ? (int)Math.Round(distance[0] * 100) : defaultValue;
                        return true;
                }
            }
            return false;
        }

        public bool TrySet(CameraProperty property, int value, bool auto = false)
        {
            if (!TryGetRange(property, out int min, out int max, out _, out _))
                return false;
            value = Math.Clamp(value, min, max);
            lock (_requestLock)
            {
                if (_request == IntPtr.Zero || _session == IntPtr.Zero)
                    return false;
                bool set;
                switch (property)
                {
                    case CameraProperty.Zoom:
                        if (auto)
                            return false;
                        float ratio = value / 100f;
                        set = ACaptureRequest_setEntry_float(_request, ACAMERA_CONTROL_ZOOM_RATIO, 1, &ratio) == ACAMERA_OK;
                        break;
                    case CameraProperty.Exposure:
                        // by hand, the sensitivity is the request's too: the template's, of no auto exposure to change it
                        byte ae = auto ? ACAMERA_CONTROL_AE_MODE_ON : ACAMERA_CONTROL_AE_MODE_OFF;
                        long ns = value * 1000L;
                        set = ACaptureRequest_setEntry_u8(_request, ACAMERA_CONTROL_AE_MODE, 1, &ae) == ACAMERA_OK
                            && (auto || ACaptureRequest_setEntry_i64(_request, ACAMERA_SENSOR_EXPOSURE_TIME, 1, &ns) == ACAMERA_OK);
                        break;
                    case CameraProperty.Focus:
                        var modes = Entry<byte>(_characteristics, ACAMERA_CONTROL_AF_AVAILABLE_MODES);
                        byte af = !auto ? ACAMERA_CONTROL_AF_MODE_OFF
                            : modes.IndexOf(ACAMERA_CONTROL_AF_MODE_CONTINUOUS_VIDEO) >= 0 ? ACAMERA_CONTROL_AF_MODE_CONTINUOUS_VIDEO : ACAMERA_CONTROL_AF_MODE_AUTO;
                        float diopters = value / 100f;
                        set = ACaptureRequest_setEntry_u8(_request, ACAMERA_CONTROL_AF_MODE, 1, &af) == ACAMERA_OK
                            && (auto || ACaptureRequest_setEntry_float(_request, ACAMERA_LENS_FOCUS_DISTANCE, 1, &diopters) == ACAMERA_OK);
                        break;
                    default:
                        return false;
                }
                // the repeating request again, of what was changed in it
                IntPtr request = _request;
                return set && ACameraCaptureSession_setRepeatingRequest(_session, IntPtr.Zero, 1, &request, IntPtr.Zero) == ACAMERA_OK;
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
            Monitor.Enter(_requestLock);
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
            FreeMetadata(_characteristics);
            _characteristics = IntPtr.Zero;
            _session = IntPtr.Zero;
            _request = IntPtr.Zero;
            Monitor.Exit(_requestLock);
        }
    }
}
