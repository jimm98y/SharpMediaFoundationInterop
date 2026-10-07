using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// The devices of GStreamer's device monitor - PipeWire's, PulseAudio's, V4L2's - of a class: Audio/Sink, Audio/Source,
    /// Video/Source. A device is told apart by its node name or path, which <see cref="CreateElement"/> finds it by again.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static class GstDevices
    {
        /// <summary>The devices of the class: each its id and name.</summary>
        public static (string Id, string Name)[] List(string deviceClass)
        {
            var devices = Gst.GetDevices(deviceClass);
            try
            {
                var result = new (string, string)[devices.Count];
                for (int i = 0; i < devices.Count; i++)
                    result[i] = (devices[i].Id, devices[i].Name);
                return result;
            }
            finally
            {
                Gst.Release(devices);
            }
        }

        /// <summary>The element of the device of the id - its source or sink - or, of no id, of the first; null where there is none.</summary>
        public static IntPtr? CreateElement(string deviceClass, string id) => CreateElement(deviceClass, id, out _);

        /// <summary>As <see cref="CreateElement(string, string)"/>, and the device's caps, the caller's to unref.</summary>
        public static IntPtr? CreateElement(string deviceClass, string id, out IntPtr caps)
        {
            caps = IntPtr.Zero;
            var devices = Gst.GetDevices(deviceClass);
            try
            {
                foreach (var device in devices)
                {
                    if (id != null && device.Id != id)
                        continue;
                    IntPtr element = Gst.CreateElement(device.Handle);
                    if (element == IntPtr.Zero)
                        return null;
                    caps = Gst.GetDeviceCaps(device.Handle);
                    return element;
                }
                return null;
            }
            finally
            {
                Gst.Release(devices);
            }
        }
    }
}
