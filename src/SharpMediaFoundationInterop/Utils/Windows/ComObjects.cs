using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// COM objects of the source generators ([GeneratedComInterface]), as the generated interop makes them: an object of
    /// a pointer, a pointer of an object, and an object let go of - what <see cref="Marshal"/> does of the runtime's own
    /// COM objects, which it does not of these. Of no reflection: trimmed and compiled ahead of time alike.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public static unsafe class ComObjects
    {
        private static readonly StrategyBasedComWrappers Wrappers = new StrategyBasedComWrappers();

        /// <summary>
        /// An object of a COM pointer, of a reference of its own - the pointer's is the caller's still. An object of its
        /// own, not one cached of the pointer: let go of with <see cref="Release"/>, it is let go of alone, and the next
        /// object of the same pointer - a sample of a decoder's pool - is a new one.
        /// </summary>
        public static T Wrap<T>(void* pointer) where T : class =>
            pointer == null ? null : (T)Wrappers.GetOrCreateObjectForComInstance((nint)pointer, CreateObjectFlags.UniqueInstance);

        /// <summary>
        /// An object of a COM pointer whose reference it takes: of an object made or handed out - the caller's to let go
        /// of - an object of its own, let go of with <see cref="Release"/> as soon as it is done with.
        /// </summary>
        public static T Take<T>(void* pointer) where T : class
        {
            if (pointer == null)
                return null;
            var value = Wrap<T>(pointer);
            Marshal.Release((nint)pointer);
            return value;
        }

        /// <summary>The pointer of an object of an interface, of a reference the caller's: let go of with <see cref="Marshal.Release"/>.</summary>
        public static void* PointerOf<T>(T value) where T : class =>
            value == null ? null : ComInterfaceMarshaller<T>.ConvertToUnmanaged(value);

        /// <summary>The IUnknown of a COM object, of a reference the caller's: let go of with <see cref="Marshal.Release"/>.</summary>
        public static nint UnknownOf(object value) =>
            value != null && ComWrappers.TryGetComInstance(value, out nint unknown) ? unknown : 0;

        /// <summary>
        /// Lets go of what an object of its own holds of its COM object - one of <see cref="Wrap"/>, <see cref="Take"/> or
        /// <see cref="MediaObjects"/> - now, not when it is collected. Of an object the generated interop made of an out or
        /// a return, cached of its pointer, nothing: it is let go of as it is collected.
        /// </summary>
        public static void Release(object value)
        {
            if (value is ComObject com)
                com.FinalRelease();
        }
    }
}
