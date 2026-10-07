using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// The Objective-C runtime, of libobjc's C functions: what AVFoundation and ScreenCaptureKit - frameworks of classes,
    /// with no C functions of their own - are reached by. A message is sent with objc_msgSend, called as a function of the
    /// method's own arguments; a class is made at run time to take a delegate's messages, of methods that are C# functions;
    /// a completion handler is a block laid out by hand. An NSInteger argument is passed as an IntPtr - nint, of the same
    /// size - and a BOOL comes back as a byte.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    internal static unsafe class ObjC
    {
        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
        private const string SystemLib = "/usr/lib/libSystem.B.dylib";

        private static readonly IntPtr ObjCHandle = NativeLibrary.Load(ObjCLib);
        private static readonly IntPtr SystemHandle = NativeLibrary.Load(SystemLib);

        /// <summary>objc_msgSend, of which each signature is a function pointer of its own.</summary>
        private static readonly IntPtr MsgSend = NativeLibrary.GetExport(ObjCHandle, "objc_msgSend");

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_getClass(string name);

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_getProtocol(string name);

        [DllImport(ObjCLib)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

        [DllImport(ObjCLib)]
        public static extern void objc_registerClassPair(IntPtr cls);

        [DllImport(ObjCLib)]
        public static extern byte class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

        [DllImport(ObjCLib)]
        public static extern byte class_addProtocol(IntPtr cls, IntPtr protocol);

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_retain(IntPtr obj);

        [DllImport(ObjCLib)]
        public static extern void objc_release(IntPtr obj);

        [DllImport(ObjCLib)]
        public static extern IntPtr objc_autoreleasePoolPush();

        [DllImport(ObjCLib)]
        public static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(SystemLib)]
        private static extern IntPtr dispatch_queue_create(string label, IntPtr attributes);

        [DllImport(SystemLib)]
        public static extern void dispatch_release(IntPtr obj);

        private static readonly ConcurrentDictionary<string, IntPtr> Selectors = new ConcurrentDictionary<string, IntPtr>();

        public static IntPtr Sel(string name) => Selectors.GetOrAdd(name, sel_registerName);

        public static IntPtr Class(string name)
        {
            IntPtr cls = objc_getClass(name);
            if (cls == IntPtr.Zero)
                throw new NotSupportedException($"No Objective-C class {name}: its framework is not loaded, or not of this macOS");
            return cls;
        }

        /// <summary>A serial dispatch queue: what a delegate is called on.</summary>
        public static IntPtr CreateQueue(string label) => dispatch_queue_create(label, IntPtr.Zero);

        /// <summary>Loads a framework, so its classes and constants are there.</summary>
        public static IntPtr LoadFramework(string name) => NativeLibrary.Load($"/System/Library/Frameworks/{name}.framework/{name}");

        /// <summary>The value of a constant a framework exports - an NSString, of a key - or zero where it has none.</summary>
        public static IntPtr Constant(IntPtr framework, string name) =>
            NativeLibrary.TryGetExport(framework, name, out IntPtr address) ? *(IntPtr*)address : IntPtr.Zero;

        #region Messages

        public static IntPtr Send(IntPtr receiver, string selector) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector));

        public static IntPtr Send(IntPtr receiver, string selector, IntPtr a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector), a);

        public static IntPtr Send(IntPtr receiver, string selector, IntPtr a, IntPtr b) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector), a, b);

        public static IntPtr Send(IntPtr receiver, string selector, IntPtr a, IntPtr b, IntPtr c) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, Sel(selector), a, b, c);

        public static IntPtr Send(IntPtr receiver, string selector, IntPtr a, nint b, IntPtr c, IntPtr* d) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint, IntPtr, IntPtr*, IntPtr>)MsgSend)(receiver, Sel(selector), a, b, c, d);

        public static IntPtr Send(IntPtr receiver, string selector, IntPtr a, IntPtr* b) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)MsgSend)(receiver, Sel(selector), a, b);

        public static nint SendNInt(IntPtr receiver, string selector) =>
            ((delegate* unmanaged<IntPtr, IntPtr, nint>)MsgSend)(receiver, Sel(selector));

        public static nint SendNInt(IntPtr receiver, string selector, IntPtr a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint>)MsgSend)(receiver, Sel(selector), a);

        public static bool SendBool(IntPtr receiver, string selector) =>
            ((delegate* unmanaged<IntPtr, IntPtr, byte>)MsgSend)(receiver, Sel(selector)) != 0;

        public static bool SendBool(IntPtr receiver, string selector, IntPtr a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)MsgSend)(receiver, Sel(selector), a) != 0;

        public static bool SendBool(IntPtr receiver, string selector, IntPtr* a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, byte>)MsgSend)(receiver, Sel(selector), a) != 0;

        public static double SendDouble(IntPtr receiver, string selector) =>
            ((delegate* unmanaged<IntPtr, IntPtr, double>)MsgSend)(receiver, Sel(selector));

        public static void SendVoid(IntPtr receiver, string selector) =>
            ((delegate* unmanaged<IntPtr, IntPtr, void>)MsgSend)(receiver, Sel(selector));

        public static void SendVoid(IntPtr receiver, string selector, IntPtr a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)MsgSend)(receiver, Sel(selector), a);

        public static void SendVoid(IntPtr receiver, string selector, IntPtr a, IntPtr b) =>
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)MsgSend)(receiver, Sel(selector), a, b);

        public static void SendVoid(IntPtr receiver, string selector, uint a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)MsgSend)(receiver, Sel(selector), a);

        public static void SendVoid(IntPtr receiver, string selector, bool a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, byte, void>)MsgSend)(receiver, Sel(selector), a ? (byte)1 : (byte)0);

        public static void SendVoid(IntPtr receiver, string selector, CMTime a) =>
            ((delegate* unmanaged<IntPtr, IntPtr, CMTime, void>)MsgSend)(receiver, Sel(selector), a);

        public static CMTime SendCMTime(IntPtr receiver, string selector)
        {
            // a struct of 24 bytes is returned through memory: of objc_msgSend_stret on Intel, of objc_msgSend on Apple's chips
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                CMTime result;
                var stret = NativeLibrary.GetExport(ObjCHandle, "objc_msgSend_stret");
                ((delegate* unmanaged<CMTime*, IntPtr, IntPtr, void>)stret)(&result, receiver, Sel(selector));
                return result;
            }
            return ((delegate* unmanaged<IntPtr, IntPtr, CMTime>)MsgSend)(receiver, Sel(selector));
        }

        /// <summary>[[cls alloc] init]: the caller's to release.</summary>
        public static IntPtr New(string className) => Send(Send(Class(className), "alloc"), "init");

        #endregion

        #region Values

        /// <summary>The text of an NSString, or null of nil.</summary>
        public static string GetString(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero)
                return null;
            IntPtr utf8 = Send(nsString, "UTF8String");
            return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
        }

        /// <summary>What an NSError says, or null of nil.</summary>
        public static string GetError(IntPtr nsError) =>
            nsError == IntPtr.Zero ? null : GetString(Send(nsError, "localizedDescription"));

        public static nint Count(IntPtr nsArray) => nsArray == IntPtr.Zero ? 0 : SendNInt(nsArray, "count");

        public static IntPtr ObjectAt(IntPtr nsArray, nint index) => Send(nsArray, "objectAtIndex:", index);

        #endregion

        #region Blocks

        private const int BLOCK_IS_GLOBAL = 1 << 28;

        /// <summary>
        /// A block, as the compiler lays one out: its class, flags, the function it calls with itself first, its descriptor -
        /// and after that, what it captured: here the handle of the managed object it calls back.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct BlockLiteral
        {
            public IntPtr Isa;
            public int Flags;
            public int Reserved;
            public IntPtr Invoke;
            public BlockDescriptor* Descriptor;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BlockDescriptor
        {
            public nuint Reserved;
            public nuint Size;
        }

        private static readonly IntPtr NSConcreteGlobalBlock = NativeLibrary.GetExport(SystemHandle, "_NSConcreteGlobalBlock");

        /// <summary>
        /// A block of the function given - whose first argument is the block, its context read of
        /// <see cref="BlockLiteral.Context"/> - in memory of its own: global, so copying and releasing it leave it be. Freed
        /// with <see cref="FreeBlock"/> once nothing can call it any more.
        /// </summary>
        public static BlockLiteral* CreateBlock(IntPtr invoke, IntPtr context)
        {
            var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            descriptor->Size = (nuint)sizeof(BlockLiteral);
            var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
            block->Isa = NSConcreteGlobalBlock;
            block->Flags = BLOCK_IS_GLOBAL;
            block->Invoke = invoke;
            block->Descriptor = descriptor;
            block->Context = context;
            return block;
        }

        public static void FreeBlock(BlockLiteral* block)
        {
            if (block == null)
                return;
            NativeMemory.Free(block->Descriptor);
            NativeMemory.Free(block);
        }

        #endregion

        /// <summary>An autorelease pool for the scope: the objects made autoreleased in it are let go of at its end.</summary>
        public readonly struct AutoreleasePool : IDisposable
        {
            private readonly IntPtr _pool;

            private AutoreleasePool(IntPtr pool) => _pool = pool;

            public static AutoreleasePool Create() => new AutoreleasePool(objc_autoreleasePoolPush());

            public void Dispose() => objc_autoreleasePoolPop(_pool);
        }

        /// <summary>The text of a CFString: a device's name or id.</summary>
        public static string GetCFString(IntPtr cfString)
        {
            if (cfString == IntPtr.Zero)
                return null;
            nint length = CFStringGetLength(cfString);
            var chars = new char[length];
            fixed (char* p = chars)
                CFStringGetCharacters(cfString, new CFRange { Location = 0, Length = length }, p);
            return new string(chars);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CFRange
        {
            public nint Location;
            public nint Length;
        }

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern nint CFStringGetLength(IntPtr str);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFStringGetCharacters(IntPtr str, CFRange range, char* buffer);
    }
}
