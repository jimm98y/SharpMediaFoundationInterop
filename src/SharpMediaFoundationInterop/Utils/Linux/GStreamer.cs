using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// GStreamer, of its C functions: what Linux's codecs and devices are made of here, as the WPF fork's Linux head plays
    /// its media. No variadic function is called - they are not to be relied on across ABIs, aarch64's above all - so
    /// properties go through gst_util_set_object_arg, and what a macro reaches in a struct is read at an offset measured
    /// once, at start, of an object whose fields are known.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal static unsafe class Gst
    {
        private const string LibGst = "libgstreamer-1.0.so.0";
        private const string LibGstApp = "libgstapp-1.0.so.0";
        private const string LibGstVideo = "libgstvideo-1.0.so.0";
        private const string LibGObject = "libgobject-2.0.so.0";
        private const string LibGLib = "libglib-2.0.so.0";

        public const int GST_STATE_NULL = 1;
        public const int GST_STATE_READY = 2;
        public const int GST_STATE_PAUSED = 3;
        public const int GST_STATE_PLAYING = 4;
        public const int GST_STATE_CHANGE_FAILURE = 0;
        public const int GST_FORMAT_TIME = 3;
        public const int GST_FLOW_OK = 0;
        private const int GST_MAP_READ = 1;
        private const uint GST_MESSAGE_ERROR = 1 << 1;
        private const int GST_PAD_SINK = 2;
        public const ulong GST_ELEMENT_FACTORY_TYPE_DECODER = 1 << 0;
        public const ulong GST_ELEMENT_FACTORY_TYPE_ENCODER = 1 << 1;
        private const int GST_RANK_MARGINAL = 64;
        public const ulong ClockTimeNone = ulong.MaxValue;

        [StructLayout(LayoutKind.Sequential)]
        public struct GstMapInfo
        {
            public IntPtr Memory;
            public int Flags;
            public byte* Data;
            public nuint Size;
            public nuint MaxSize;
            private fixed long _userData[4];
            private fixed long _reserved[4];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GList
        {
            public IntPtr Data;
            public GList* Next;
            public GList* Prev;
        }

        #region Initialization

        private static readonly object InitLock = new object();
        private static bool? _available;
        private static string _unavailableReason;
        private static int _ptsOffset = -1;

        /// <summary>Whether GStreamer is here and starts: of libgstreamer-1.0 and its app library, installed.</summary>
        public static bool IsAvailable
        {
            get
            {
                lock (InitLock)
                {
                    if (_available == null)
                    {
                        try
                        {
                            if (gst_init_check(IntPtr.Zero, IntPtr.Zero, out IntPtr error) == 0)
                            {
                                _unavailableReason = TakeGError(error) ?? "gst_init failed";
                                _available = false;
                            }
                            else
                            {
                                _ptsOffset = MeasurePtsOffset();
                                // appsrc and appsink are of the app library, of gstreamer1.0-plugins-base
                                if (!NativeLibrary.TryLoad(LibGstApp, out _) || !NativeLibrary.TryLoad(LibGstVideo, out _))
                                    _unavailableReason = "libgstapp-1.0 or libgstvideo-1.0 is not installed";
                                else if (_ptsOffset <= 0)
                                    _unavailableReason = "the layout of GstBuffer is not one this knows";
                                _available = _unavailableReason == null;
                            }
                        }
                        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                        {
                            _unavailableReason = ex.Message;
                            _available = false;
                        }
                    }
                    return _available.Value;
                }
            }
        }

        /// <summary>Why GStreamer is not here, where it is not: for the exceptions thrown of it.</summary>
        public static string UnavailableReason => IsAvailable ? null : _unavailableReason;

        public static void EnsureAvailable()
        {
            if (!IsAvailable)
                throw new PlatformNotSupportedException($"GStreamer is not available: {_unavailableReason}. Install libgstreamer1.0-0 and gstreamer1.0-plugins-base.");
        }

        /// <summary>
        /// The offset of GST_BUFFER_PTS in a GstBuffer: past its GstMiniObject, whose size is an ABI detail. A new buffer's
        /// pts, dts, duration, offset and offset_end are all GST_CLOCK_TIME_NONE or GST_BUFFER_OFFSET_NONE - all bits set -
        /// so the first five such words in a row are those, pts first.
        /// </summary>
        private static int MeasurePtsOffset()
        {
            IntPtr buffer = gst_buffer_new();
            try
            {
                for (int offset = IntPtr.Size; offset + 5 * 8 <= 256; offset += 4)
                {
                    bool all = true;
                    for (int i = 0; i < 5 && all; i++)
                        all = Marshal.ReadInt64(buffer, offset + i * 8) == -1;
                    if (all)
                        return offset;
                }
                return -1;
            }
            finally
            {
                gst_mini_object_unref(buffer);
            }
        }

        #endregion

        #region Pipelines

        /// <summary>A pipeline of a gst-launch description: its errors thrown, of GStreamer's own message.</summary>
        public static IntPtr Launch(string description)
        {
            IntPtr pipeline = gst_parse_launch(description, out IntPtr error);
            string message = TakeGError(error);
            if (pipeline == IntPtr.Zero || message != null)
            {
                if (pipeline != IntPtr.Zero)
                    gst_object_unref(pipeline);
                throw new NotSupportedException($"GStreamer cannot make '{description}': {message}");
            }
            return pipeline;
        }

        /// <summary>A bin of a description, its unlinked pads ghosted: a part of a pipeline made of elements as well.</summary>
        public static IntPtr LaunchBin(string description)
        {
            IntPtr bin = gst_parse_bin_from_description(description, 1, out IntPtr error);
            string message = TakeGError(error);
            if (bin == IntPtr.Zero || message != null)
            {
                if (bin != IntPtr.Zero)
                    gst_object_unref(bin);
                throw new NotSupportedException($"GStreamer cannot make '{description}': {message}");
            }
            return bin;
        }

        /// <summary>Sets a property of an element, of its value as text - of any type, an enum's by its nick - where it has one.</summary>
        public static bool SetProperty(IntPtr element, string name, string value)
        {
            // the GObjectClass, the first field of the GTypeInstance an object starts with
            IntPtr cls = Marshal.ReadIntPtr(element);
            if (g_object_class_find_property(cls, name) == IntPtr.Zero)
                return false;
            gst_util_set_object_arg(element, name, value);
            return true;
        }

        public static bool HasProperty(IntPtr element, string name) => g_object_class_find_property(Marshal.ReadIntPtr(element), name) != IntPtr.Zero;

        /// <summary>Whether there is an element of the name here: of its plugin, installed.</summary>
        public static bool HasElement(string factoryName)
        {
            if (!IsAvailable)
                return false;
            IntPtr factory = gst_element_factory_find(factoryName);
            if (factory == IntPtr.Zero)
                return false;
            gst_object_unref(factory);
            return true;
        }

        /// <summary>The first of the elements named that is here; null of none.</summary>
        public static string FirstElement(IEnumerable<string> factoryNames)
        {
            foreach (var name in factoryNames)
            {
                if (HasElement(name))
                    return name;
            }
            return null;
        }

        /// <summary>
        /// Whether there is a decoder here that decodebin would pick for the caps: of a rank of marginal or more, its sink
        /// taking them.
        /// </summary>
        public static bool HasDecoderFor(string caps)
        {
            if (!IsAvailable)
                return false;
            IntPtr gstCaps = gst_caps_from_string(caps);
            if (gstCaps == IntPtr.Zero)
                return false;
            IntPtr all = gst_element_factory_list_get_elements(GST_ELEMENT_FACTORY_TYPE_DECODER, GST_RANK_MARGINAL);
            IntPtr matching = gst_element_factory_list_filter(all, gstCaps, GST_PAD_SINK, 0);
            bool found = matching != IntPtr.Zero;
            gst_plugin_feature_list_free(matching);
            gst_plugin_feature_list_free(all);
            gst_mini_object_unref(gstCaps);
            return found;
        }

        /// <summary>The message of the first error on the bus, taken off it; null of none. The bus's other messages are left be.</summary>
        public static string TakeError(IntPtr bus)
        {
            IntPtr message = gst_bus_pop_filtered(bus, GST_MESSAGE_ERROR);
            if (message == IntPtr.Zero)
                return null;
            try
            {
                gst_message_parse_error(message, out IntPtr error, out IntPtr debug);
                string text = TakeGError(error);
                if (debug != IntPtr.Zero)
                {
                    string detail = Marshal.PtrToStringUTF8(debug);
                    g_free(debug);
                    if (Log.DebugEnabled)
                        Log.Debug($"GStreamer: {text} ({detail})");
                }
                return text ?? "GStreamer error";
            }
            finally
            {
                gst_mini_object_unref(message);
            }
        }

        /// <summary>The message of a GError, which is freed: its domain, code, then message.</summary>
        private static string TakeGError(IntPtr error)
        {
            if (error == IntPtr.Zero)
                return null;
            string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8));
            g_error_free(error);
            return message;
        }

        #endregion

        #region Buffers

        /// <summary>A buffer of the bytes - a prefix, then the data - its time given in 100 ns ticks, or none.</summary>
        public static IntPtr CreateBuffer(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, long? ticks, long? durationTicks = null)
        {
            IntPtr buffer = gst_buffer_new_allocate(IntPtr.Zero, (nuint)(prefix.Length + data.Length), IntPtr.Zero);
            if (buffer == IntPtr.Zero)
                throw new OutOfMemoryException("GStreamer has no buffer for the sample");
            fixed (byte* p = prefix)
                gst_buffer_fill(buffer, 0, p, (nuint)prefix.Length);
            fixed (byte* d = data)
                gst_buffer_fill(buffer, (nuint)prefix.Length, d, (nuint)data.Length);
            if (ticks.HasValue)
                Marshal.WriteInt64(buffer, _ptsOffset, ticks.Value * 100);
            if (durationTicks.HasValue)
                Marshal.WriteInt64(buffer, _ptsOffset + 16, durationTicks.Value * 100);
            return buffer;
        }

        /// <summary>A buffer's time, in 100 ns ticks; null where it has none.</summary>
        public static long? GetTime(IntPtr buffer)
        {
            long pts = Marshal.ReadInt64(buffer, _ptsOffset);
            return pts == -1 ? null : pts / 100;
        }

        /// <summary>Maps a buffer to be read; <see cref="Unmap"/> after.</summary>
        public static bool Map(IntPtr buffer, out GstMapInfo info)
        {
            info = default;
            fixed (GstMapInfo* p = &info)
                return gst_buffer_map(buffer, p, GST_MAP_READ) != 0;
        }

        public static void Unmap(IntPtr buffer, ref GstMapInfo info)
        {
            fixed (GstMapInfo* p = &info)
                gst_buffer_unmap(buffer, p);
        }

        /// <summary>The bytes of a buffer, copied.</summary>
        public static byte[] ToArray(IntPtr buffer)
        {
            if (!Map(buffer, out var info))
                return Array.Empty<byte>();
            try
            {
                return new ReadOnlySpan<byte>(info.Data, (int)info.Size).ToArray();
            }
            finally
            {
                Unmap(buffer, ref info);
            }
        }

        /// <summary>The strides and plane offsets of a video buffer: its GstVideoMeta's, where it has one; false where not.</summary>
        public static bool GetVideoLayout(IntPtr buffer, Span<int> strides, Span<long> offsets)
        {
            IntPtr meta = gst_buffer_get_meta(buffer, gst_video_meta_api_get_type());
            if (meta == IntPtr.Zero)
                return false;
            // GstVideoMeta: its GstMeta, buffer, flags, format, id, width, height, n_planes, offset[4], stride[4]
            int planes = Marshal.ReadInt32(meta, 2 * IntPtr.Size + IntPtr.Size + 5 * 4);
            int offsetsAt = Align(2 * IntPtr.Size + IntPtr.Size + 6 * 4, IntPtr.Size);
            int stridesAt = offsetsAt + 4 * IntPtr.Size;
            for (int i = 0; i < Math.Min(planes, strides.Length); i++)
            {
                offsets[i] = IntPtr.Size == 8 ? Marshal.ReadInt64(meta, offsetsAt + i * 8) : Marshal.ReadInt32(meta, offsetsAt + i * 4);
                strides[i] = Marshal.ReadInt32(meta, stridesAt + i * 4);
            }
            return true;
        }

        private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

        #endregion

        #region Caps

        /// <summary>An int of the first structure of the caps; null where it has none, or one not fixed.</summary>
        public static int? GetCapsInt(IntPtr caps, string field)
        {
            IntPtr structure = gst_caps_get_structure(caps, 0);
            if (structure == IntPtr.Zero || gst_structure_get_int(structure, field, out int value) == 0)
                return null;
            return value;
        }

        /// <summary>The bytes of a buffer field of the first structure of the caps - codec_data, say; null where it has none.</summary>
        public static byte[] GetCapsBuffer(IntPtr caps, string field)
        {
            IntPtr structure = gst_caps_get_structure(caps, 0);
            if (structure == IntPtr.Zero)
                return null;
            IntPtr value = gst_structure_get_value(structure, field);
            if (value == IntPtr.Zero)
                return null;
            IntPtr buffer = g_value_get_boxed(value);
            return buffer == IntPtr.Zero ? null : ToArray(buffer);
        }

        public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        #endregion

        #region Devices

        /// <summary>A device of GStreamer's device monitor: what is shown of it, what it is told apart by, and itself, retained.</summary>
        public readonly record struct Device(string Id, string Name, IntPtr Handle);

        /// <summary>
        /// The devices of the class given - Audio/Sink, Audio/Source, Video/Source - each retained, to be released with
        /// <see cref="Release"/>. A device's id is its node name, of PipeWire's or PulseAudio's, or its path, of V4L2's;
        /// failing those, its name.
        /// </summary>
        public static List<Device> GetDevices(string deviceClass)
        {
            var result = new List<Device>();
            if (!IsAvailable)
                return result;
            IntPtr monitor = gst_device_monitor_new();
            try
            {
                gst_device_monitor_add_filter(monitor, deviceClass, IntPtr.Zero);
                IntPtr list = gst_device_monitor_get_devices(monitor);
                for (var node = (GList*)list; node != null; node = node->Next)
                {
                    IntPtr device = node->Data;
                    string name = TakeString(gst_device_get_display_name(device));
                    string id = DeviceId(device) ?? name;
                    // one device two providers report - PipeWire's and PulseAudio's of one sink - is listed once
                    if (result.Exists(d => d.Id == id))
                        gst_object_unref(device);
                    else
                        result.Add(new Device(id, name, device));
                }
                g_list_free(list);
            }
            finally
            {
                gst_object_unref(monitor);
            }
            return result;
        }

        private static string DeviceId(IntPtr device)
        {
            IntPtr properties = gst_device_get_properties(device);
            if (properties == IntPtr.Zero)
                return null;
            try
            {
                foreach (var field in new[] { "node.name", "device.name", "api.v4l2.path", "device.path", "object.path" })
                {
                    IntPtr value = gst_structure_get_string(properties, field);
                    if (value != IntPtr.Zero)
                        return Marshal.PtrToStringUTF8(value);
                }
                return null;
            }
            finally
            {
                gst_structure_free(properties);
            }
        }

        public static void Release(List<Device> devices)
        {
            foreach (var device in devices)
                gst_object_unref(device.Handle);
        }

        /// <summary>An element of the device: its source or sink, as the device's provider makes it.</summary>
        public static IntPtr CreateElement(IntPtr device) => gst_device_create_element(device, null);

        public static IntPtr GetDeviceCaps(IntPtr device) => gst_device_get_caps(device);

        /// <summary>The structures of the caps, each as text.</summary>
        public static List<string> CapsStructures(IntPtr caps)
        {
            var result = new List<string>();
            uint count = gst_caps_get_size(caps);
            for (uint i = 0; i < count; i++)
            {
                IntPtr structure = gst_caps_get_structure(caps, i);
                result.Add(TakeString(gst_structure_to_string(structure)));
            }
            return result;
        }

        /// <summary>Of the caps' structure at the index: its name and an int field, where fixed.</summary>
        public static (string Name, int? Width, int? Height) CapsStructure(IntPtr caps, uint index)
        {
            IntPtr structure = gst_caps_get_structure(caps, index);
            string name = Marshal.PtrToStringUTF8(gst_structure_get_name(structure));
            int? width = gst_structure_get_int(structure, "width", out int w) != 0 ? w : null;
            int? height = gst_structure_get_int(structure, "height", out int h) != 0 ? h : null;
            return (name, width, height);
        }

        public static uint CapsSize(IntPtr caps) => gst_caps_get_size(caps);

        private static string TakeString(IntPtr text)
        {
            if (text == IntPtr.Zero)
                return null;
            string value = Marshal.PtrToStringUTF8(text);
            g_free(text);
            return value;
        }

        #endregion

        #region Native

        [DllImport(LibGst)] private static extern int gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);
        [DllImport(LibGst)] private static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
        [DllImport(LibGst)] private static extern IntPtr gst_parse_bin_from_description([MarshalAs(UnmanagedType.LPUTF8Str)] string description, int ghostUnlinkedPads, out IntPtr error);
        [DllImport(LibGst)] public static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGst)] public static extern int gst_bin_add(IntPtr bin, IntPtr element);
        [DllImport(LibGst)] public static extern int gst_element_link(IntPtr source, IntPtr destination);
        [DllImport(LibGst)] public static extern IntPtr gst_pipeline_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGst)] public static extern void gst_object_unref(IntPtr obj);
        [DllImport(LibGst)] public static extern IntPtr gst_object_ref(IntPtr obj);
        [DllImport(LibGst)] public static extern void gst_mini_object_unref(IntPtr obj);
        [DllImport(LibGst)] public static extern int gst_element_set_state(IntPtr element, int state);
        [DllImport(LibGst)] public static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeoutNs);
        [DllImport(LibGst)] public static extern IntPtr gst_element_get_bus(IntPtr element);
        [DllImport(LibGst)] public static extern int gst_element_send_event(IntPtr element, IntPtr evt);
        [DllImport(LibGst)] public static extern IntPtr gst_event_new_flush_start();
        [DllImport(LibGst)] public static extern IntPtr gst_event_new_flush_stop(int resetTime);
        [DllImport(LibGst)] public static extern int gst_element_query_position(IntPtr element, int format, out long cur);
        [DllImport(LibGst)] private static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGst)] private static extern IntPtr gst_element_factory_list_get_elements(ulong type, int minRank);
        [DllImport(LibGst)] private static extern IntPtr gst_element_factory_list_filter(IntPtr list, IntPtr caps, int direction, int subsetOnly);
        [DllImport(LibGst)] private static extern void gst_plugin_feature_list_free(IntPtr list);
        [DllImport(LibGst)] private static extern void gst_util_set_object_arg(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(LibGst)] private static extern IntPtr gst_bus_pop_filtered(IntPtr bus, uint types);
        [DllImport(LibGst)] private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
        [DllImport(LibGst)] private static extern IntPtr gst_buffer_new();
        [DllImport(LibGst)] private static extern IntPtr gst_buffer_new_allocate(IntPtr allocator, nuint size, IntPtr parameters);
        [DllImport(LibGst)] private static extern nuint gst_buffer_fill(IntPtr buffer, nuint offset, void* src, nuint size);
        [DllImport(LibGst)] private static extern int gst_buffer_map(IntPtr buffer, GstMapInfo* info, int flags);
        [DllImport(LibGst)] private static extern void gst_buffer_unmap(IntPtr buffer, GstMapInfo* info);
        [DllImport(LibGst)] private static extern IntPtr gst_buffer_get_meta(IntPtr buffer, nuint api);
        [DllImport(LibGst)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
        [DllImport(LibGst)] public static extern IntPtr gst_sample_get_caps(IntPtr sample);
        [DllImport(LibGst)] private static extern IntPtr gst_caps_from_string([MarshalAs(UnmanagedType.LPUTF8Str)] string caps);
        [DllImport(LibGst)] private static extern uint gst_caps_get_size(IntPtr caps);
        [DllImport(LibGst)] private static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);
        [DllImport(LibGst)] private static extern int gst_structure_get_int(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);
        [DllImport(LibGst)] private static extern IntPtr gst_structure_get_value(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field);
        [DllImport(LibGst)] private static extern IntPtr gst_structure_get_string(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field);
        [DllImport(LibGst)] private static extern IntPtr gst_structure_get_name(IntPtr structure);
        [DllImport(LibGst)] private static extern IntPtr gst_structure_to_string(IntPtr structure);
        [DllImport(LibGst)] private static extern void gst_structure_free(IntPtr structure);
        [DllImport(LibGst)] private static extern IntPtr gst_device_monitor_new();
        [DllImport(LibGst)] private static extern uint gst_device_monitor_add_filter(IntPtr monitor, [MarshalAs(UnmanagedType.LPUTF8Str)] string classes, IntPtr caps);
        [DllImport(LibGst)] private static extern IntPtr gst_device_monitor_get_devices(IntPtr monitor);
        [DllImport(LibGst)] private static extern IntPtr gst_device_get_display_name(IntPtr device);
        [DllImport(LibGst)] private static extern IntPtr gst_device_get_properties(IntPtr device);
        [DllImport(LibGst)] private static extern IntPtr gst_device_get_caps(IntPtr device);
        [DllImport(LibGst)] private static extern IntPtr gst_device_create_element(IntPtr device, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGstApp)] public static extern int gst_app_src_push_buffer(IntPtr appsrc, IntPtr buffer);
        [DllImport(LibGstApp)] public static extern int gst_app_src_end_of_stream(IntPtr appsrc);
        [DllImport(LibGstApp)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appsink, ulong timeoutNs);
        [DllImport(LibGstApp)] public static extern int gst_app_sink_is_eos(IntPtr appsink);
        [DllImport(LibGstVideo)] private static extern nuint gst_video_meta_api_get_type();
        [DllImport(LibGObject)] private static extern IntPtr g_object_class_find_property(IntPtr objectClass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(LibGObject)] private static extern IntPtr g_value_get_boxed(IntPtr value);
        [DllImport(LibGLib)] private static extern void g_free(IntPtr mem);
        [DllImport(LibGLib)] private static extern void g_error_free(IntPtr error);
        [DllImport(LibGLib)] private static extern void g_list_free(IntPtr list);

        #endregion
    }

    /// <summary>
    /// A pipeline of an appsrc named src, what GStreamer does of it, and an appsink named sink: what each codec and device
    /// of Linux's is. Samples go in at the source and come out of the sink; its errors are thrown, of GStreamer's message.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed unsafe class GstAppPipeline : IDisposable
    {
        public IntPtr Pipeline { get; private set; }
        public IntPtr Source { get; private set; }
        public IntPtr Sink { get; private set; }
        private IntPtr _bus;

        /// <summary>A pipeline of the description: its src and sink, of their names, where it has them.</summary>
        public GstAppPipeline(string description)
        {
            Gst.EnsureAvailable();
            Pipeline = Gst.Launch(description);
            Attach();
        }

        /// <summary>
        /// A pipeline of elements made elsewhere - a device's source before, or its sink after - linked to a bin of the
        /// description between them.
        /// </summary>
        public GstAppPipeline(IntPtr first, string description, IntPtr last)
        {
            Gst.EnsureAvailable();
            Pipeline = Gst.gst_pipeline_new(null);
            IntPtr bin = Gst.LaunchBin(description);
            Gst.gst_bin_add(Pipeline, bin);
            bool linked = true;
            if (first != IntPtr.Zero)
            {
                Gst.gst_bin_add(Pipeline, first);
                linked &= Gst.gst_element_link(first, bin) != 0;
            }
            if (last != IntPtr.Zero)
            {
                Gst.gst_bin_add(Pipeline, last);
                linked &= Gst.gst_element_link(bin, last) != 0;
            }
            Attach();
            if (!linked)
            {
                Dispose();
                throw new NotSupportedException($"The device cannot be linked to '{description}'");
            }
        }

        private void Attach()
        {
            Source = Gst.gst_bin_get_by_name(Pipeline, "src");
            Sink = Gst.gst_bin_get_by_name(Pipeline, "sink");
            _bus = Gst.gst_element_get_bus(Pipeline);
        }

        /// <summary>An element of the pipeline, of its name: the caller's to unref.</summary>
        public IntPtr Get(string name) => Gst.gst_bin_get_by_name(Pipeline, name);

        public void Play() => SetState(Gst.GST_STATE_PLAYING);

        public void Pause() => SetState(Gst.GST_STATE_PAUSED);

        public void Stop() => Gst.gst_element_set_state(Pipeline, Gst.GST_STATE_NULL);

        private void SetState(int state)
        {
            if (Gst.gst_element_set_state(Pipeline, state) == Gst.GST_STATE_CHANGE_FAILURE)
                throw new InvalidOperationException($"The GStreamer pipeline did not start: {Gst.TakeError(_bus) ?? "no reason given"}");
        }

        /// <summary>Waits for the pipeline to reach the state it was asked for, up to the time given; false where it does not.</summary>
        public bool WaitForState(TimeSpan timeout) =>
            Gst.gst_element_get_state(Pipeline, out _, out _, (ulong)timeout.Ticks * 100) != Gst.GST_STATE_CHANGE_FAILURE;

        /// <summary>Pushes a sample in: a prefix - a start code, say - and the data, of the time given, or none.</summary>
        public bool Push(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, long? ticks, long? durationTicks = null)
        {
            ThrowIfError();
            IntPtr buffer = Gst.CreateBuffer(prefix, data, ticks, durationTicks);
            // the buffer is the source's now, pushed or not
            return Gst.gst_app_src_push_buffer(Source, buffer) == Gst.GST_FLOW_OK;
        }

        /// <summary>The next sample out, waiting up to the time given; zero where there is none. The caller's to unref.</summary>
        public IntPtr Pull(TimeSpan timeout)
        {
            ThrowIfError();
            return Gst.gst_app_sink_try_pull_sample(Sink, timeout <= TimeSpan.Zero ? 0 : (ulong)timeout.Ticks * 100);
        }

        public bool IsEndOfStream => Gst.gst_app_sink_is_eos(Sink) != 0;

        /// <summary>Tells the source there is no more: what the pipeline holds comes out, then the end.</summary>
        public void EndOfStream() => Gst.gst_app_src_end_of_stream(Source);

        /// <summary>Lets go of everything in the pipeline, and of an end of stream: the source takes samples again.</summary>
        public void Flush()
        {
            Gst.gst_element_send_event(Pipeline, Gst.gst_event_new_flush_start());
            Gst.gst_element_send_event(Pipeline, Gst.gst_event_new_flush_stop(1));
        }

        /// <summary>Where the pipeline is, in 100 ns ticks; null where it cannot say.</summary>
        public long? Position => Gst.gst_element_query_position(Pipeline, Gst.GST_FORMAT_TIME, out long ns) != 0 && ns >= 0 ? ns / 100 : null;

        public void ThrowIfError()
        {
            string error = Gst.TakeError(_bus);
            if (error != null)
                throw new InvalidOperationException($"GStreamer: {error}");
        }

        public void Dispose()
        {
            if (Pipeline == IntPtr.Zero)
                return;
            Gst.gst_element_set_state(Pipeline, Gst.GST_STATE_NULL);
            if (Source != IntPtr.Zero)
                Gst.gst_object_unref(Source);
            if (Sink != IntPtr.Zero)
                Gst.gst_object_unref(Sink);
            if (_bus != IntPtr.Zero)
                Gst.gst_object_unref(_bus);
            Gst.gst_object_unref(Pipeline);
            Pipeline = Source = Sink = _bus = IntPtr.Zero;
        }
    }
}
