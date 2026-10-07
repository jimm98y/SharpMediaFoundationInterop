using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using static SharpMediaFoundationInterop.Utils.AppleNative;
using static SharpMediaFoundationInterop.Utils.ObjC;

namespace SharpMediaFoundationInterop.Input
{
    /// <summary>
    /// Captures a camera on macOS and iOS, of AVFoundation: an AVCaptureSession of the camera's widest format, at its
    /// highest frame rate, its frames handed out as NV12. The first time, the system asks the user whether the app may use
    /// the camera - on iOS of the app's NSCameraUsageDescription, which its Info.plist must have; <see cref="Initialize"/>
    /// waits for them, and throws <see cref="UnauthorizedAccessException"/> where they say no.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    public sealed unsafe class AVFoundationCapture : IMediaVideoSource
    {
        /// <summary>How long <see cref="ReadSample"/> waits for a frame before it gives up and returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 2000;

        private static readonly IntPtr AVFoundation = LoadFramework("AVFoundation");
        private static readonly ConcurrentDictionary<IntPtr, AVFoundationCapture> Captures = new ConcurrentDictionary<IntPtr, AVFoundationCapture>();
        private static readonly Lazy<IntPtr> DelegateClass = new Lazy<IntPtr>(CreateDelegateClass);

        private readonly string _uniqueID;
        private readonly object _frameLock = new object();
        private readonly AutoResetEvent _frameReady = new AutoResetEvent(false);
        private IntPtr _frame;
        private long _frameTime;
        private long _firstTime = -1;

        private IntPtr _session;
        private IntPtr _input;
        private IntPtr _output;
        private IntPtr _delegate;
        private IntPtr _queue;
        private bool _disposed;

        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth => Width;
        public uint OriginalHeight => Height;
        public Guid OutputFormat => MediaFormats.NV12;
        public uint OutputSize => Width * Height * 3 / 2;

        /// <param name="uniqueID">The camera's id, as <see cref="Enumerate"/> gives it; null for the first.</param>
        public AVFoundationCapture(string uniqueID = null)
        {
            _uniqueID = uniqueID;
        }

        /// <summary>The cameras: built in, connected, and of a nearby iPhone - each its id and its name.</summary>
        public static (string UniqueID, string Name)[] Enumerate()
        {
            using var pool = AutoreleasePool.Create();
            var result = new List<(string, string)>();
            IntPtr devices = Devices();
            for (nint i = 0; i < Count(devices); i++)
            {
                IntPtr device = ObjectAt(devices, i);
                result.Add((GetString(Send(device, "uniqueID")), GetString(Send(device, "localizedName"))));
            }
            return result.ToArray();
        }

        /// <summary>The cameras, of a discovery session of the kinds there are: an NSArray, autoreleased.</summary>
        private static IntPtr Devices()
        {
            var types = new List<IntPtr>();
            // the physical cameras, of macOS and iOS both: a type of neither platform's is not exported there, and is skipped
            foreach (var name in new[] { "AVCaptureDeviceTypeBuiltInWideAngleCamera", "AVCaptureDeviceTypeBuiltInUltraWideCamera", "AVCaptureDeviceTypeBuiltInTelephotoCamera",
                "AVCaptureDeviceTypeBuiltInTrueDepthCamera", "AVCaptureDeviceTypeExternal", "AVCaptureDeviceTypeContinuityCamera", "AVCaptureDeviceTypeDeskViewCamera" })
            {
                IntPtr type = Constant(AVFoundation, name);
                if (type != IntPtr.Zero)
                    types.Add(type);
            }
            // before macOS 14, a camera connected is of a type since renamed
            if (OperatingSystem.IsMacOS() && !OperatingSystem.IsMacOSVersionAtLeast(14))
            {
                IntPtr external = Constant(AVFoundation, "AVCaptureDeviceTypeExternalUnknown");
                if (external != IntPtr.Zero)
                    types.Add(external);
            }

            var array = types.ToArray();
            IntPtr typeArray;
            fixed (IntPtr* p = array)
                typeArray = Send(Class("NSArray"), "arrayWithObjects:count:", (IntPtr)p, (nint)array.Length);
            IntPtr discovery = Send(Class("AVCaptureDeviceDiscoverySession"), "discoverySessionWithDeviceTypes:mediaType:position:",
                typeArray, Constant(AVFoundation, "AVMediaTypeVideo"), (nint)0);
            return Send(discovery, "devices");
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session != IntPtr.Zero)
                return;

            RequestAccess();

            using var pool = AutoreleasePool.Create();
            IntPtr device = FindDevice() ?? throw new InvalidOperationException(_uniqueID == null ? "There is no camera" : $"There is no camera {_uniqueID}");

            IntPtr error = IntPtr.Zero;
            IntPtr input = Send(Class("AVCaptureDeviceInput"), "deviceInputWithDevice:error:", device, &error);
            if (input == IntPtr.Zero)
                throw new InvalidOperationException($"The camera cannot be opened: {GetError(error)}");
            _input = objc_retain(input);

            _session = New("AVCaptureSession");
            SendVoid(_session, "beginConfiguration");
            if (!SendBool(_session, "canAddInput:", _input))
                throw new InvalidOperationException("The camera cannot be captured");
            SendVoid(_session, "addInput:", _input);

            // of the input, before the format: a session's preset would choose the format otherwise
            SelectBestFormat(device);

            _output = New("AVCaptureVideoDataOutput");
            IntPtr settings = CreateDictionary();
            IntPtr pixelFormat = CreateNumber((int)kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange);
            SetValue(settings, kCVPixelBufferPixelFormatTypeKey, pixelFormat);
            SendVoid(_output, "setVideoSettings:", settings);
            CFRelease(pixelFormat);
            CFRelease(settings);
            SendVoid(_output, "setAlwaysDiscardsLateVideoFrames:", true);

            _delegate = Send(DelegateClass.Value, "new");
            Captures[_delegate] = this;
            _queue = CreateQueue("SharpMediaFoundationInterop.Camera");
            SendVoid(_output, "setSampleBufferDelegate:queue:", _delegate, _queue);
            if (!SendBool(_session, "canAddOutput:", _output))
                throw new InvalidOperationException("The camera's frames cannot be read");
            SendVoid(_session, "addOutput:", _output);
            SendVoid(_session, "commitConfiguration");

            SendVoid(_session, "startRunning");
        }

        private IntPtr? FindDevice()
        {
            if (_uniqueID != null)
            {
                IntPtr id = CreateString(_uniqueID);
                IntPtr device = Send(Class("AVCaptureDevice"), "deviceWithUniqueID:", id);
                CFRelease(id);
                return device == IntPtr.Zero ? null : device;
            }
            IntPtr devices = Devices();
            if (Count(devices) > 0)
                return ObjectAt(devices, 0);
            IntPtr fallback = Send(Class("AVCaptureDevice"), "defaultDeviceWithMediaType:", Constant(AVFoundation, "AVMediaTypeVideo"));
            return fallback == IntPtr.Zero ? null : fallback;
        }

        /// <summary>
        /// The widest format, of those the tallest - as of Windows' camera capture, which compares their sizes so - and of
        /// those the highest frame rate: made the camera's, and its size this capture's.
        /// </summary>
        private void SelectBestFormat(IntPtr device)
        {
            IntPtr formats = Send(device, "formats");
            IntPtr best = IntPtr.Zero, bestRange = IntPtr.Zero;
            long bestSizeKey = 0;
            double bestRate = 0;
            CMVideoDimensions bestSize = default;
            for (nint i = 0; i < Count(formats); i++)
            {
                IntPtr format = ObjectAt(formats, i);
                var size = CMVideoFormatDescriptionGetDimensions(Send(format, "formatDescription"));
                long sizeKey = ((long)size.Width << 32) | (uint)size.Height;
                IntPtr ranges = Send(format, "videoSupportedFrameRateRanges");
                for (nint j = 0; j < Count(ranges); j++)
                {
                    IntPtr range = ObjectAt(ranges, j);
                    double rate = SendDouble(range, "maxFrameRate");
                    if (sizeKey > bestSizeKey || (sizeKey == bestSizeKey && rate > bestRate))
                    {
                        best = format;
                        bestRange = range;
                        bestSizeKey = sizeKey;
                        bestRate = rate;
                        bestSize = size;
                    }
                }
            }

            if (best != IntPtr.Zero)
            {
                IntPtr error = IntPtr.Zero;
                if (SendBool(device, "lockForConfiguration:", &error))
                {
                    SendVoid(device, "setActiveFormat:", best);
                    SendVoid(device, "setActiveVideoMinFrameDuration:", SendCMTime(bestRange, "minFrameDuration"));
                    SendVoid(device, "unlockForConfiguration");
                }
                else if (Log.WarnEnabled)
                {
                    Log.Warn($"The camera keeps its own format: {GetError(error)}");
                }
            }

            var active = CMVideoFormatDescriptionGetDimensions(Send(Send(device, "activeFormat"), "formatDescription"));
            if (active.Width == 0)
                active = bestSize;
            Width = (uint)active.Width;
            Height = (uint)active.Height;
        }

        /// <summary>
        /// The next frame, NV12, of <see cref="Width"/> by <see cref="Height"/>, and its time, from the first frame's: waiting
        /// for one up to <see cref="ReadTimeoutInMilliseconds"/>. Frames not read in time are let go of, the newest kept.
        /// </summary>
        public bool ReadSample(byte[] sampleBytes, out long timestamp)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            timestamp = 0;
            if (_session == IntPtr.Zero)
                throw new InvalidOperationException("The capture is to be initialized first.");

            IntPtr frame;
            while (true)
            {
                lock (_frameLock)
                {
                    frame = _frame;
                    _frame = IntPtr.Zero;
                    timestamp = _frameTime;
                }
                if (frame != IntPtr.Zero)
                    break;
                if (!_frameReady.WaitOne(ReadTimeoutInMilliseconds))
                    return false;
            }

            try
            {
                return PixelBuffers.CopyNV12(frame, sampleBytes, Width, Height, out _, out _);
            }
            finally
            {
                CFRelease(frame);
            }
        }

        /// <summary>Asks the user for the camera where they have not been asked, and waits for their answer.</summary>
        private static void RequestAccess()
        {
            IntPtr mediaType = Constant(AVFoundation, "AVMediaTypeVideo");
            IntPtr deviceClass = Class("AVCaptureDevice");
            nint status = SendNInt(deviceClass, "authorizationStatusForMediaType:", mediaType);
            if (status == 3) // authorized
                return;
            if (status == 0) // not determined
            {
                var answer = new AccessAnswer();
                var handle = GCHandle.Alloc(answer);
                // left, as the system may hold the block after it is called: once a process
                var block = CreateBlock((IntPtr)(delegate* unmanaged<BlockLiteral*, byte, void>)&OnAccessAnswered, GCHandle.ToIntPtr(handle));
                SendVoid(deviceClass, "requestAccessForMediaType:completionHandler:", mediaType, (IntPtr)block);
                answer.Done.Wait();
                handle.Free();
                if (answer.Granted)
                    return;
            }
            throw new UnauthorizedAccessException("The app may not use the camera: allow it in System Settings, Privacy & Security, Camera");
        }

        private sealed class AccessAnswer
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public volatile bool Granted;
        }

        [UnmanagedCallersOnly]
        private static void OnAccessAnswered(BlockLiteral* block, byte granted)
        {
            if (GCHandle.FromIntPtr(block->Context).Target is AccessAnswer answer)
            {
                answer.Granted = granted != 0;
                answer.Done.Set();
            }
        }

        /// <summary>The class of the sample buffer delegate: an NSObject whose captureOutput:didOutputSampleBuffer:fromConnection: is <see cref="OnFrame"/>.</summary>
        private static IntPtr CreateDelegateClass()
        {
            IntPtr cls = objc_allocateClassPair(Class("NSObject"), "SharpMediaFoundationInteropCameraDelegate", 0);
            class_addMethod(cls, Sel("captureOutput:didOutputSampleBuffer:fromConnection:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnFrame, "v@:@@@");
            IntPtr protocol = objc_getProtocol("AVCaptureVideoDataOutputSampleBufferDelegate");
            if (protocol != IntPtr.Zero)
                class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
            return cls;
        }

        /// <summary>A frame, of the delegate's queue: kept, retained, in place of any not read.</summary>
        [UnmanagedCallersOnly]
        private static void OnFrame(IntPtr self, IntPtr selector, IntPtr output, IntPtr sampleBuffer, IntPtr connection)
        {
            try
            {
                if (!Captures.TryGetValue(self, out var capture))
                    return;
                IntPtr image = CMSampleBufferGetImageBuffer(sampleBuffer);
                if (image == IntPtr.Zero)
                    return;

                long time = CMSampleBufferGetPresentationTimeStamp(sampleBuffer).ToTicks();
                CFRetain(image);
                IntPtr previous;
                lock (capture._frameLock)
                {
                    if (capture._firstTime < 0)
                        capture._firstTime = time;
                    previous = capture._frame;
                    capture._frame = image;
                    capture._frameTime = time - capture._firstTime;
                }
                if (previous != IntPtr.Zero)
                    CFRelease(previous);
                capture._frameReady.Set();
            }
            catch (Exception ex)
            {
                if (Log.ErrorEnabled)
                    Log.Error($"A camera frame could not be taken: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            using var pool = AutoreleasePool.Create();
            if (_session != IntPtr.Zero)
            {
                SendVoid(_session, "stopRunning");
                objc_release(_session);
                _session = IntPtr.Zero;
            }
            if (_output != IntPtr.Zero)
            {
                SendVoid(_output, "setSampleBufferDelegate:queue:", IntPtr.Zero, IntPtr.Zero);
                objc_release(_output);
                _output = IntPtr.Zero;
            }
            if (_delegate != IntPtr.Zero)
            {
                Captures.TryRemove(_delegate, out _);
                objc_release(_delegate);
                _delegate = IntPtr.Zero;
            }
            if (_input != IntPtr.Zero)
            {
                objc_release(_input);
                _input = IntPtr.Zero;
            }
            if (_queue != IntPtr.Zero)
            {
                dispatch_release(_queue);
                _queue = IntPtr.Zero;
            }
            lock (_frameLock)
            {
                if (_frame != IntPtr.Zero)
                    CFRelease(_frame);
                _frame = IntPtr.Zero;
            }
            _frameReady.Dispose();
        }
    }
}
