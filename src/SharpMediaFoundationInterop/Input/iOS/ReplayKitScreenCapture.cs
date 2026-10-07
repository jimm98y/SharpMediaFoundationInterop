using System;
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
    /// Captures the app's own screen on iOS, of ReplayKit: iOS lets an app record what it shows, not the rest of the
    /// system - that needs a broadcast upload extension of the app's, beyond a library - and asks the user, each time,
    /// whether it may. The frames ReplayKit hands out are converted to 32 bit BGRA, as the other platforms' screen captures
    /// hand them out, bottom-up or top-down as <see cref="BottomUp"/> says. <see cref="Initialize"/> waits for the user's
    /// answer and the first frame, and throws <see cref="UnauthorizedAccessException"/> where they say no.
    /// </summary>
    [SupportedOSPlatform("ios16.0")]
    public sealed unsafe class ReplayKitScreenCapture : IMediaVideoSource
    {
        /// <summary>How long <see cref="ReadSample(byte[], out long)"/> waits for a new frame before it returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 40;

        /// <summary>Whether frames are handed out bottom-up, the first row the bottom one, rather than top-down.</summary>
        public bool BottomUp { get; set; } = true;

        /// <summary>How long the user is given to allow the recording.</summary>
        public TimeSpan AllowTimeout { get; set; } = TimeSpan.FromMinutes(2);

        private static readonly IntPtr ReplayKit = LoadFramework("ReplayKit");
        private const nint RPSampleBufferTypeVideo = 1;

        private readonly object _frameLock = new object();
        private readonly AutoResetEvent _frameReady = new AutoResetEvent(false);
        private readonly List<IntPtr> _blocks = new List<IntPtr>();
        private GCHandle _self;
        private IntPtr _frame;
        private long _frameTime;
        private long _firstTime = -1;
        private IntPtr _transfer;
        private IntPtr _bgra;
        private bool _started;
        private bool _disposed;

        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth => Width;
        public uint OriginalHeight => Height;
        public Guid OutputFormat => MediaFormats.ARGB32;
        public uint OutputSize => Width * Height * 4;

        private static IntPtr Recorder => Send(Class("RPScreenRecorder"), "sharedRecorder");

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            if (!SendBool(Recorder, "isAvailable"))
                throw new NotSupportedException("ReplayKit cannot record here: not on this device, or the screen is mirrored");

            _self = GCHandle.Alloc(this, GCHandleType.Weak);
            var handler = CreateBlock((IntPtr)(delegate* unmanaged<BlockLiteral*, IntPtr, nint, IntPtr, void>)&OnSample, GCHandle.ToIntPtr(_self));
            var started = new Completion();
            var startedHandle = GCHandle.Alloc(started);
            var completion = CreateBlock((IntPtr)(delegate* unmanaged<BlockLiteral*, IntPtr, void>)&OnCompleted, GCHandle.ToIntPtr(startedHandle));
            _blocks.Add((IntPtr)handler);
            _blocks.Add((IntPtr)completion);
            try
            {
                SendVoid(Recorder, "startCaptureWithHandler:completionHandler:", (IntPtr)handler, (IntPtr)completion);
                if (!started.Done.Wait(AllowTimeout))
                    throw new TimeoutException("The user did not answer whether the app may record its screen");
            }
            finally
            {
                completion->Context = IntPtr.Zero;
                startedHandle.Free();
            }
            if (started.Error != null)
                throw new UnauthorizedAccessException($"The app may not record its screen: {started.Error}");
            _started = true;

            // the first frame tells the size
            if (!_frameReady.WaitOne(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("ReplayKit gave no frame");
            lock (_frameLock)
            {
                Width = (uint)CVPixelBufferGetWidth(_frame);
                Height = (uint)CVPixelBufferGetHeight(_frame);
            }
        }

        public bool ReadSample(byte[] sampleBytes, out long timestamp) => ReadSample(sampleBytes, BottomUp, out timestamp);

        /// <summary>
        /// The next frame, 32 bit BGRA of <see cref="Width"/> by <see cref="Height"/>, bottom-up or top-down, and its time,
        /// from the first frame's; false where there is no new one in <see cref="ReadTimeoutInMilliseconds"/>.
        /// </summary>
        public bool ReadSample(byte[] sampleBytes, bool bottomUp, out long timestamp)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            timestamp = 0;
            if (!_started)
                throw new InvalidOperationException("The capture is to be initialized first.");

            IntPtr frame;
            lock (_frameLock)
            {
                frame = _frame;
                _frame = IntPtr.Zero;
                timestamp = _frameTime;
            }
            if (frame == IntPtr.Zero)
            {
                if (!_frameReady.WaitOne(ReadTimeoutInMilliseconds))
                    return false;
                lock (_frameLock)
                {
                    frame = _frame;
                    _frame = IntPtr.Zero;
                    timestamp = _frameTime;
                }
                if (frame == IntPtr.Zero)
                    return false;
            }

            try
            {
                if (CVPixelBufferGetPixelFormatType(frame) == kCVPixelFormatType_32BGRA)
                    return PixelBuffers.CopyBgra(frame, sampleBytes, Width, Height, bottomUp);
                return PixelBuffers.CopyBgra(ToBgra(frame), sampleBytes, Width, Height, bottomUp);
            }
            finally
            {
                CFRelease(frame);
            }
        }

        /// <summary>The frame as BGRA, of VideoToolbox's pixel transfer: into a buffer of this capture's, made once.</summary>
        private IntPtr ToBgra(IntPtr frame)
        {
            if (_transfer == IntPtr.Zero && VTPixelTransferSessionCreate(IntPtr.Zero, out _transfer) != 0)
                throw new InvalidOperationException("No pixel transfer session, to convert the screen's frames");
            if (_bgra == IntPtr.Zero && CVPixelBufferCreate(IntPtr.Zero, Width, Height, kCVPixelFormatType_32BGRA, IntPtr.Zero, out _bgra) != 0)
                throw new InvalidOperationException("No pixel buffer for the screen's frames");
            int status = VTPixelTransferSessionTransferImage(_transfer, frame, _bgra);
            if (status != 0)
                throw new InvalidOperationException($"The screen's frame could not be converted: {FourCCString(status)}");
            return _bgra;
        }

        private sealed class Completion
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Error;
        }

        [UnmanagedCallersOnly]
        private static void OnCompleted(BlockLiteral* block, IntPtr error)
        {
            if (block->Context == IntPtr.Zero || GCHandle.FromIntPtr(block->Context).Target is not Completion completion)
                return;
            completion.Error = GetError(error);
            completion.Done.Set();
        }

        /// <summary>A sample of ReplayKit's: of the screen, kept, retained, in place of any not read; of sound, let be.</summary>
        [UnmanagedCallersOnly]
        private static void OnSample(BlockLiteral* block, IntPtr sampleBuffer, nint type, IntPtr error)
        {
            try
            {
                if (type != RPSampleBufferTypeVideo || error != IntPtr.Zero || block->Context == IntPtr.Zero ||
                    GCHandle.FromIntPtr(block->Context).Target is not ReplayKitScreenCapture capture)
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
                    Log.Error($"A screen frame could not be taken: {ex}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_started)
            {
                var stopped = new Completion();
                var handle = GCHandle.Alloc(stopped);
                var block = CreateBlock((IntPtr)(delegate* unmanaged<BlockLiteral*, IntPtr, void>)&OnCompleted, GCHandle.ToIntPtr(handle));
                _blocks.Add((IntPtr)block);
                SendVoid(Recorder, "stopCaptureWithHandler:", (IntPtr)block);
                stopped.Done.Wait(TimeSpan.FromSeconds(5));
                block->Context = IntPtr.Zero;
                handle.Free();
            }
            foreach (var block in _blocks)
            {
                // the handler is let go of by ReplayKit as the capture stops: what calls it late finds no target
                ((BlockLiteral*)block)->Context = IntPtr.Zero;
            }
            if (_self.IsAllocated)
                _self.Free();
            lock (_frameLock)
            {
                if (_frame != IntPtr.Zero)
                    CFRelease(_frame);
                _frame = IntPtr.Zero;
            }
            if (_bgra != IntPtr.Zero)
                CFRelease(_bgra);
            if (_transfer != IntPtr.Zero)
            {
                VTPixelTransferSessionInvalidate(_transfer);
                CFRelease(_transfer);
            }
            _frameReady.Dispose();
        }
    }
}
