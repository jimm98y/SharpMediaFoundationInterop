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
    /// Captures a display on macOS, of ScreenCaptureKit: an SCStream of the display whole, at its size in pixels, as 32 bit
    /// BGRA with the cursor in it, bottom-up or top-down as <see cref="BottomUp"/> says. As DXGI's duplication does, it
    /// hands a frame out only where the screen changed. The app needs the user's leave to record the screen: where it has
    /// none, <see cref="Initialize"/> asks for it - macOS sends the user to System Settings - and throws
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    [SupportedOSPlatform("macos12.3")]
    public sealed unsafe class ScreenCaptureKitCapture : IMediaVideoSource
    {
        /// <summary>How long <see cref="ReadSample(byte[], out long)"/> waits for the screen to change before it returns false.</summary>
        public int ReadTimeoutInMilliseconds { get; set; } = 40;

        /// <summary>
        /// Whether frames are handed out bottom-up, the first row the bottom one - as a video transform takes RGB - rather
        /// than top-down, as a bitmap is.
        /// </summary>
        public bool BottomUp { get; set; } = true;

        /// <summary>The frames a second the stream is asked for, at most.</summary>
        public uint MaxFrameRate { get; set; } = 60;

        private static readonly IntPtr ScreenCaptureKit = LoadFramework("ScreenCaptureKit");
        private static readonly ConcurrentDictionary<IntPtr, ScreenCaptureKitCapture> Captures = new ConcurrentDictionary<IntPtr, ScreenCaptureKitCapture>();
        private static readonly Lazy<IntPtr> OutputClass = new Lazy<IntPtr>(CreateOutputClass);
        private static readonly IntPtr FrameInfoStatus = Constant(ScreenCaptureKit, "SCStreamFrameInfoStatus");

        private readonly uint _displayID;
        private readonly object _frameLock = new object();
        private readonly AutoResetEvent _frameReady = new AutoResetEvent(false);
        private readonly List<IntPtr> _blocks = new List<IntPtr>();
        private IntPtr _frame;
        private long _frameTime;
        private long _firstTime = -1;

        private IntPtr _stream;
        private IntPtr _output;
        private IntPtr _queue;
        private bool _disposed;

        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth => Width;
        public uint OriginalHeight => Height;
        /// <summary>BGRA in memory: Media Foundation's ARGB32, as DXGI's duplication hands it out.</summary>
        public Guid OutputFormat => MediaFormats.ARGB32;
        public uint OutputSize => Width * Height * 4;

        /// <param name="displayID">The display's CGDirectDisplayID, as <see cref="Enumerate"/> gives it; 0 for the main one.</param>
        public ScreenCaptureKitCapture(uint displayID = 0)
        {
            _displayID = displayID;
        }

        /// <summary>
        /// The active displays, the main one first: each its CGDirectDisplayID and a name of its kind and size, as macOS gives
        /// a display no name but of AppKit's, which is of the main thread alone.
        /// </summary>
        public static (uint DisplayID, string Name)[] Enumerate()
        {
            uint* ids = stackalloc uint[32];
            if (CGGetActiveDisplayList(32, ids, out uint count) != 0)
                return Array.Empty<(uint, string)>();

            uint main = CGMainDisplayID();
            var result = new List<(uint, string)>();
            int external = 0;
            for (int i = 0; i < count; i++)
            {
                uint id = ids[i];
                GetPixelSize(id, out uint width, out uint height);
                string name = CGDisplayIsBuiltin(id) != 0 ? "Built-in Display" : $"Display {++external}";
                var entry = (id, $"{name} ({width}x{height})");
                if (id == main)
                    result.Insert(0, entry);
                else
                    result.Add(entry);
            }
            return result.ToArray();
        }

        private static void GetPixelSize(uint displayID, out uint width, out uint height)
        {
            IntPtr mode = CGDisplayCopyDisplayMode(displayID);
            width = mode == IntPtr.Zero ? 0 : (uint)CGDisplayModeGetPixelWidth(mode);
            height = mode == IntPtr.Zero ? 0 : (uint)CGDisplayModeGetPixelHeight(mode);
            if (mode != IntPtr.Zero)
                CGDisplayModeRelease(mode);
        }

        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream != IntPtr.Zero)
                return;

            if (CGPreflightScreenCaptureAccess() == 0)
            {
                CGRequestScreenCaptureAccess();
                throw new UnauthorizedAccessException("The app may not record the screen: allow it in System Settings, Privacy & Security, Screen & System Audio Recording, then start it again");
            }

            using var pool = AutoreleasePool.Create();
            uint displayID = _displayID != 0 ? _displayID : CGMainDisplayID();

            // the displays ScreenCaptureKit can capture, and of them the one asked for
            var content = new Completion();
            Call(content, (IntPtr)(delegate* unmanaged<BlockLiteral*, IntPtr, IntPtr, void>)&OnContent,
                block => SendVoid(Class("SCShareableContent"), "getShareableContentWithCompletionHandler:", block));
            if (content.Result == IntPtr.Zero)
                throw new InvalidOperationException($"The screen's content could not be read: {content.Error}");

            IntPtr display = IntPtr.Zero;
            try
            {
                IntPtr displays = Send(content.Result, "displays");
                for (nint i = 0; i < Count(displays); i++)
                {
                    IntPtr candidate = ObjectAt(displays, i);
                    if ((uint)SendNInt(candidate, "displayID") == displayID)
                        display = candidate;
                }
                if (display == IntPtr.Zero)
                    throw new InvalidOperationException($"There is no display {displayID}");

                GetPixelSize(displayID, out uint width, out uint height);
                if (width == 0 || height == 0)
                {
                    width = (uint)SendNInt(display, "width");
                    height = (uint)SendNInt(display, "height");
                }
                Width = width;
                Height = height;

                IntPtr noWindows = Send(Class("NSArray"), "array");
                IntPtr filter = Send(Send(Class("SCContentFilter"), "alloc"), "initWithDisplay:excludingWindows:", display, noWindows);

                IntPtr configuration = New("SCStreamConfiguration");
                SendVoid(configuration, "setWidth:", (IntPtr)(nint)width);
                SendVoid(configuration, "setHeight:", (IntPtr)(nint)height);
                SendVoid(configuration, "setPixelFormat:", kCVPixelFormatType_32BGRA);
                SendVoid(configuration, "setShowsCursor:", true);
                SendVoid(configuration, "setQueueDepth:", (IntPtr)(nint)5);
                SendVoid(configuration, "setMinimumFrameInterval:", new CMTime { Value = 1, Timescale = (int)Math.Max(MaxFrameRate, 1), Flags = 1 });

                _stream = Send(Send(Class("SCStream"), "alloc"), "initWithFilter:configuration:delegate:", filter, configuration, IntPtr.Zero);
                objc_release(filter);
                objc_release(configuration);
            }
            finally
            {
                objc_release(content.Result);
            }

            _output = Send(OutputClass.Value, "new");
            Captures[_output] = this;
            _queue = CreateQueue("SharpMediaFoundationInterop.Screen");
            IntPtr error = IntPtr.Zero;
            // SCStreamOutputTypeScreen, 0
            if ((byte)Send(_stream, "addStreamOutput:type:sampleHandlerQueue:error:", _output, 0, _queue, &error) == 0)
                throw new InvalidOperationException($"The screen's frames cannot be read: {GetError(error)}");

            var started = new Completion();
            Call(started, (IntPtr)(delegate* unmanaged<BlockLiteral*, IntPtr, void>)&OnDone,
                block => SendVoid(_stream, "startCaptureWithCompletionHandler:", block));
            if (started.Error != null)
                throw new InvalidOperationException($"The screen capture did not start: {started.Error}");
        }

        /// <summary>The next frame, as <see cref="BottomUp"/> says; false where the screen has not changed in <see cref="ReadTimeoutInMilliseconds"/>.</summary>
        public bool ReadSample(byte[] sampleBytes, out long timestamp) => ReadSample(sampleBytes, BottomUp, out timestamp);

        /// <summary>
        /// The next frame, 32 bit BGRA of <see cref="Width"/> by <see cref="Height"/>, bottom-up or top-down, and its time,
        /// from the first frame's; false where the screen has not changed in <see cref="ReadTimeoutInMilliseconds"/>.
        /// </summary>
        public bool ReadSample(byte[] sampleBytes, bool bottomUp, out long timestamp)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            timestamp = 0;
            if (_stream == IntPtr.Zero)
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
                return PixelBuffers.CopyBgra(frame, sampleBytes, Width, Height, bottomUp);
            }
            finally
            {
                CFRelease(frame);
            }
        }

        #region Completion handlers

        /// <summary>What a completion handler was called with: an object, retained, or an error.</summary>
        private sealed class Completion
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public IntPtr Result;
            public string Error;
        }

        /// <summary>
        /// Calls a method of a completion handler and waits for it. The block is kept until the capture is disposed, as the
        /// framework may let go of it only after it has called it.
        /// </summary>
        private void Call(Completion completion, IntPtr invoke, Action<IntPtr> call)
        {
            var handle = GCHandle.Alloc(completion);
            var block = CreateBlock(invoke, GCHandle.ToIntPtr(handle));
            lock (_blocks)
                _blocks.Add((IntPtr)block);
            try
            {
                call((IntPtr)block);
                if (!completion.Done.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("ScreenCaptureKit did not answer");
            }
            finally
            {
                // what calls it late finds no target, and does nothing
                block->Context = IntPtr.Zero;
                handle.Free();
            }
        }

        [UnmanagedCallersOnly]
        private static void OnContent(BlockLiteral* block, IntPtr content, IntPtr error)
        {
            if (block->Context == IntPtr.Zero || GCHandle.FromIntPtr(block->Context).Target is not Completion completion)
                return;
            completion.Result = content == IntPtr.Zero ? IntPtr.Zero : objc_retain(content);
            completion.Error = GetError(error);
            completion.Done.Set();
        }

        [UnmanagedCallersOnly]
        private static void OnDone(BlockLiteral* block, IntPtr error)
        {
            if (block->Context == IntPtr.Zero || GCHandle.FromIntPtr(block->Context).Target is not Completion completion)
                return;
            completion.Error = GetError(error);
            completion.Done.Set();
        }

        #endregion

        /// <summary>The class of the stream's output: an NSObject whose stream:didOutputSampleBuffer:ofType: is <see cref="OnFrame"/>.</summary>
        private static IntPtr CreateOutputClass()
        {
            IntPtr cls = objc_allocateClassPair(Class("NSObject"), "SharpMediaFoundationInteropScreenOutput", 0);
            class_addMethod(cls, Sel("stream:didOutputSampleBuffer:ofType:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, nint, void>)&OnFrame, "v@:@@q");
            IntPtr protocol = objc_getProtocol("SCStreamOutput");
            if (protocol != IntPtr.Zero)
                class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
            return cls;
        }

        /// <summary>
        /// A frame of the stream, of its queue: kept, retained, in place of any not read, where it is a new picture -
        /// SCFrameStatusComplete - rather than word that the screen is as it was.
        /// </summary>
        [UnmanagedCallersOnly]
        private static void OnFrame(IntPtr self, IntPtr selector, IntPtr stream, IntPtr sampleBuffer, nint type)
        {
            try
            {
                if (type != 0 || !Captures.TryGetValue(self, out var capture))
                    return;
                if (FrameStatus(sampleBuffer) != 0)
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

        /// <summary>The SCFrameStatus of the frame's attachments: 0, complete, of a new picture; -1 where it has none.</summary>
        private static long FrameStatus(IntPtr sampleBuffer)
        {
            IntPtr attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, 0);
            if (attachments == IntPtr.Zero || CFArrayGetCount(attachments) == 0 || FrameInfoStatus == IntPtr.Zero)
                return -1;
            IntPtr status = CFDictionaryGetValue(CFArrayGetValueAtIndex(attachments, 0), FrameInfoStatus);
            return status == IntPtr.Zero ? -1 : SendNInt(status, "integerValue");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            using var pool = AutoreleasePool.Create();
            if (_stream != IntPtr.Zero)
            {
                try
                {
                    var stopped = new Completion();
                    Call(stopped, (IntPtr)(delegate* unmanaged<BlockLiteral*, IntPtr, void>)&OnDone,
                        block => SendVoid(_stream, "stopCaptureWithCompletionHandler:", block));
                }
                catch (TimeoutException)
                {
                    // stopped or not, it is let go of
                }
                objc_release(_stream);
                _stream = IntPtr.Zero;
            }
            if (_output != IntPtr.Zero)
            {
                Captures.TryRemove(_output, out _);
                objc_release(_output);
                _output = IntPtr.Zero;
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
            lock (_blocks)
            {
                foreach (var block in _blocks)
                    FreeBlock((BlockLiteral*)block);
                _blocks.Clear();
            }
            _frameReady.Dispose();
        }
    }
}
