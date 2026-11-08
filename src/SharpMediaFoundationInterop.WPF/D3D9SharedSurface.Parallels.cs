using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A workaround of Parallels' display driver. WPF opens its front buffer on the surface's device every frame - D3DImage's
    /// present calls CreateTexture of that device with the front buffer's shared handle, copies into it, and releases it -
    /// and Parallels' driver leaks each such open: a surface's worth of GPU memory a frame, which nothing frees until the
    /// process ends. On a Parallels adapter alone, CreateTexture is hooked for that: a texture this device opened of a handle
    /// is kept, and handed out again for the same handle instead of opened again; a new handle - WPF's front buffer made
    /// again - is opened, and the one kept before released.
    /// </summary>
    /// <remarks>
    /// The vtable hooked is d3d9's, shared by every device of the process, WPF's own among them: a device not of this class,
    /// or of another adapter, is passed through as it is. The hook stays for the life of the process.
    /// </remarks>
    internal sealed unsafe partial class D3D9SharedSurface
    {
        private const int GetAdapterIdentifierSlot = 5;

        // the surfaces of the devices the workaround is of, by their device; the original CreateTexture, by vtable
        private static readonly Dictionary<IntPtr, D3D9SharedSurface> ParallelsDevices = new();
        private static readonly Dictionary<IntPtr, IntPtr> OriginalCreateTexture = new();

        // the texture WPF opened on this device last, and the handle it was opened by
        private IntPtr _openedHandle;
        private IntPtr _opened;

        [DllImport("kernel32.dll")]
        private static extern bool VirtualProtect(IntPtr address, nuint size, uint protect, out uint old);

        /// <summary>Of a device of Parallels' adapter, the workaround: its CreateTexture hooked.</summary>
        private void WorkAroundParallels(uint adapter)
        {
            if (!IsParallels(_d3d, adapter))
                return;
            HookCreateTexture(_device);
            lock (ParallelsDevices)
                ParallelsDevices[_device] = this;
        }

        /// <summary>The device let go of by the workaround, and the texture it kept released.</summary>
        private void ReleaseParallelsWorkaround()
        {
            if (_device != IntPtr.Zero)
            {
                lock (ParallelsDevices)
                    ParallelsDevices.Remove(_device);
            }
            lock (this)
                Release(ref _opened);
        }

        /// <summary>
        /// Whether the adapter is Parallels' virtual one: its user mode driver prl_umdd.dll, or its description. Its vendor
        /// id is no PCI one, and is not relied on.
        /// </summary>
        private static bool IsParallels(IntPtr d3d, uint adapter)
        {
            // D3DADAPTER_IDENTIFIER9: Driver, then Description, of 512 characters each, ...
            const int size = 1104, driverOffset = 0, descriptionOffset = 512;
            byte* identifier = stackalloc byte[size];
            var getAdapterIdentifier = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, byte*, int>)Slot(d3d, GetAdapterIdentifierSlot);
            if (getAdapterIdentifier(d3d, adapter, 0, identifier) < 0)
                return false;
            string driver = Marshal.PtrToStringAnsi((IntPtr)(identifier + driverOffset)) ?? "";
            string description = Marshal.PtrToStringAnsi((IntPtr)(identifier + descriptionOffset)) ?? "";
            return driver.StartsWith("prl_", StringComparison.OrdinalIgnoreCase)
                || description.IndexOf("Parallels", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void HookCreateTexture(IntPtr device)
        {
            IntPtr* vtable = *(IntPtr**)device;
            lock (OriginalCreateTexture)
            {
                if (OriginalCreateTexture.ContainsKey((IntPtr)vtable))
                    return;
                OriginalCreateTexture[(IntPtr)vtable] = vtable[CreateTextureSlot];
                IntPtr hook = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)&CreateTextureHook;
                VirtualProtect((IntPtr)(vtable + CreateTextureSlot), (nuint)IntPtr.Size, 0x04 /* PAGE_READWRITE */, out uint old);
                vtable[CreateTextureSlot] = hook;
                VirtualProtect((IntPtr)(vtable + CreateTextureSlot), (nuint)IntPtr.Size, old, out _);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int CreateTextureHook(IntPtr self, uint width, uint height, uint levels, uint usage, int format, int pool, IntPtr* texture, IntPtr* sharedHandle)
        {
            IntPtr original;
            lock (OriginalCreateTexture)
                original = OriginalCreateTexture[(IntPtr)(*(IntPtr**)self)];
            var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)original;

            D3D9SharedSurface owner;
            lock (ParallelsDevices)
                ParallelsDevices.TryGetValue(self, out owner);
            if (owner == null || sharedHandle == null || *sharedHandle == IntPtr.Zero)
                return create(self, width, height, levels, usage, format, pool, texture, sharedHandle);

            lock (owner)
            {
                if (owner._opened != IntPtr.Zero && owner._openedHandle == *sharedHandle)
                {
                    Marshal.AddRef(owner._opened);
                    *texture = owner._opened;
                    return 0;
                }
                int hr = create(self, width, height, levels, usage, format, pool, texture, sharedHandle);
                if (hr >= 0)
                {
                    if (owner._opened != IntPtr.Zero)
                        Marshal.Release(owner._opened);
                    owner._opened = *texture;
                    owner._openedHandle = *sharedHandle;
                    Marshal.AddRef(owner._opened);
                }
                return hr;
            }
        }
    }
}
