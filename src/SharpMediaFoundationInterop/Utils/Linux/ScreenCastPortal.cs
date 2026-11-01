using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// The ScreenCast interface of xdg-desktop-portal, over libdbus-1 as the WPF fork's Linux head reaches the portal: what a
    /// Wayland client is let capture the screen by. A session is made, a monitor asked for - the user picks it in the
    /// desktop's own dialog - and started; the portal then hands out a PipeWire remote, as a file descriptor, and the node
    /// of the stream on it, which pipewiresrc reads. The session ends as this is disposed.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed unsafe class ScreenCastPortal : IDisposable
    {
        private const string Lib = "libdbus-1.so.3";
        private const string PortalName = "org.freedesktop.portal.Desktop";
        private const string PortalPath = "/org/freedesktop/portal/desktop";
        private const string ScreenCast = "org.freedesktop.portal.ScreenCast";

        private const int DBUS_BUS_SESSION = 0;
        private const int TypeInvalid = 0;
        private const int TypeString = 's';
        private const int TypeObjectPath = 'o';
        private const int TypeUInt32 = 'u';
        private const int TypeInt32 = 'i';
        private const int TypeBoolean = 'b';
        private const int TypeArray = 'a';
        private const int TypeVariant = 'v';
        private const int TypeDictEntry = 'e';
        private const int TypeStruct = 'r';
        private const int TypeUnixFd = 'h';

        /// <summary>A DBusMessageIter's room: the struct is of 72 bytes on 64-bit systems, opaque, and allocated by its user.</summary>
        private const int IterSize = 128;

        private IntPtr _connection;
        private string _session;

        /// <summary>The PipeWire remote's file descriptor, for pipewiresrc's fd: the caller's once read.</summary>
        public int PipeWireFd { get; private set; } = -1;

        /// <summary>The PipeWire node of the stream the user chose, for pipewiresrc's path.</summary>
        public uint NodeId { get; private set; }

        /// <summary>The stream's size, where the portal says it; 0 by 0 where not.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>
        /// Asks the portal for a monitor to capture, the cursor in it: the user picks one in the desktop's dialog, which this
        /// waits for, up to <paramref name="timeout"/>. Throws <see cref="UnauthorizedAccessException"/> where they cancel.
        /// </summary>
        public void Start(TimeSpan timeout)
        {
            var error = stackalloc byte[64];
            dbus_error_init(error);
            _connection = dbus_bus_get_private(DBUS_BUS_SESSION, error);
            if (_connection == IntPtr.Zero)
                throw new PlatformNotSupportedException($"No D-Bus session bus, which the screen capture portal is reached by: {ErrorMessage(error)}");
            dbus_connection_set_exit_on_disconnect(_connection, 0);

            var created = Request("CreateSession", timeout, (w, token) =>
            {
                w.OpenDict();
                w.Entry("handle_token", token);
                w.Entry("session_handle_token", "smfi_" + Guid.NewGuid().ToString("N"));
                w.Close();
            });
            _session = created.TryGetValue("session_handle", out var handle) ? (string)handle : null;
            if (_session == null)
                throw new InvalidOperationException("The screen capture portal made no session");

            // a monitor, one, the cursor drawn in it: as DXGI's duplication and ScreenCaptureKit give it
            Request("SelectSources", timeout, (w, token) =>
            {
                w.ObjectPath(_session);
                w.OpenDict();
                w.Entry("handle_token", token);
                w.Entry("types", 1u);
                w.Entry("multiple", false);
                w.Entry("cursor_mode", 2u);
                w.Close();
            });

            var started = Request("Start", timeout, (w, token) =>
            {
                w.ObjectPath(_session);
                w.String("");
                w.OpenDict();
                w.Entry("handle_token", token);
                w.Close();
            });
            if (started.TryGetValue("streams", out var streams) && streams is List<(uint Node, int Width, int Height)> list && list.Count > 0)
                (NodeId, Width, Height) = list[0];
            else
                throw new InvalidOperationException("The screen capture portal started no stream");

            PipeWireFd = OpenPipeWireRemote();
        }

        /// <summary>
        /// Calls a method of the portal's that answers with a Request, and waits for its Response signal: of its results, the
        /// ones this reads. The signal is listened for before the call, at the path the portal will make of the token.
        /// </summary>
        private Dictionary<string, object> Request(string method, TimeSpan timeout, Action<Writer, string> arguments)
        {
            string token = "smfi_" + Guid.NewGuid().ToString("N");
            string sender = Marshal.PtrToStringUTF8(dbus_bus_get_unique_name(_connection)).TrimStart(':').Replace('.', '_');
            string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

            var error = stackalloc byte[64];
            dbus_error_init(error);
            dbus_bus_add_match(_connection, $"type='signal',interface='org.freedesktop.portal.Request',member='Response',path='{requestPath}'", error);
            if (dbus_error_is_set(error) != 0)
                throw new InvalidOperationException($"The portal's answer cannot be listened for: {ErrorMessage(error)}");

            IntPtr message = dbus_message_new_method_call(PortalName, PortalPath, ScreenCast, method);
            using (var writer = new Writer(message))
                arguments(writer, token);
            IntPtr reply = dbus_connection_send_with_reply_and_block(_connection, message, 30000, error);
            dbus_message_unref(message);
            if (reply == IntPtr.Zero)
                throw new InvalidOperationException($"The screen capture portal refused {method}: {ErrorMessage(error)}");
            dbus_message_unref(reply);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                dbus_connection_read_write(_connection, 100);
                IntPtr signal;
                while ((signal = dbus_connection_pop_message(_connection)) != IntPtr.Zero)
                {
                    try
                    {
                        if (dbus_message_is_signal(signal, "org.freedesktop.portal.Request", "Response") == 0 ||
                            Marshal.PtrToStringUTF8(dbus_message_get_path(signal)) != requestPath)
                            continue;
                        var results = ReadResponse(signal, out uint response);
                        if (response == 1)
                            throw new UnauthorizedAccessException("The screen capture was cancelled: the user did not share a screen");
                        if (response != 0)
                            throw new InvalidOperationException($"The screen capture portal ended {method} without a result ({response})");
                        return results;
                    }
                    finally
                    {
                        dbus_message_unref(signal);
                    }
                }
            }
            throw new TimeoutException($"The screen capture portal did not answer {method}");
        }

        /// <summary>A Response's code and results: of these, session_handle and the streams, each its node and size.</summary>
        private static Dictionary<string, object> ReadResponse(IntPtr signal, out uint response)
        {
            var results = new Dictionary<string, object>();
            var iter = stackalloc byte[IterSize];
            response = 2;
            if (dbus_message_iter_init(signal, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeUInt32)
                return results;
            uint code = 2;
            dbus_message_iter_get_basic(iter, &code);
            response = code;
            if (dbus_message_iter_next(iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeArray)
                return results;

            var entries = stackalloc byte[IterSize];
            var entry = stackalloc byte[IterSize];
            var variant = stackalloc byte[IterSize];
            dbus_message_iter_recurse(iter, entries);
            while (dbus_message_iter_get_arg_type(entries) == TypeDictEntry)
            {
                dbus_message_iter_recurse(entries, entry);
                string key = GetString(entry);
                dbus_message_iter_next(entry);
                dbus_message_iter_recurse(entry, variant);
                if (key == "session_handle")
                    results[key] = GetString(variant);
                else if (key == "streams")
                    results[key] = ReadStreams(variant);
                dbus_message_iter_next(entries);
            }
            return results;
        }

        /// <summary>The streams, a(ua{sv}): each a node, and of its properties the size, (ii).</summary>
        private static List<(uint, int, int)> ReadStreams(byte* array)
        {
            var streams = new List<(uint, int, int)>();
            if (dbus_message_iter_get_arg_type(array) != TypeArray)
                return streams;
            var item = stackalloc byte[IterSize];
            var fields = stackalloc byte[IterSize];
            var properties = stackalloc byte[IterSize];
            var entry = stackalloc byte[IterSize];
            var variant = stackalloc byte[IterSize];
            var size = stackalloc byte[IterSize];
            dbus_message_iter_recurse(array, item);
            while (dbus_message_iter_get_arg_type(item) == TypeStruct)
            {
                dbus_message_iter_recurse(item, fields);
                uint node = 0;
                dbus_message_iter_get_basic(fields, &node);
                int width = 0, height = 0;
                if (dbus_message_iter_next(fields) != 0 && dbus_message_iter_get_arg_type(fields) == TypeArray)
                {
                    dbus_message_iter_recurse(fields, properties);
                    while (dbus_message_iter_get_arg_type(properties) == TypeDictEntry)
                    {
                        dbus_message_iter_recurse(properties, entry);
                        string key = GetString(entry);
                        dbus_message_iter_next(entry);
                        if (key == "size")
                        {
                            dbus_message_iter_recurse(entry, variant);
                            if (dbus_message_iter_get_arg_type(variant) == TypeStruct)
                            {
                                dbus_message_iter_recurse(variant, size);
                                dbus_message_iter_get_basic(size, &width);
                                dbus_message_iter_next(size);
                                dbus_message_iter_get_basic(size, &height);
                            }
                        }
                        dbus_message_iter_next(properties);
                    }
                }
                streams.Add((node, width, height));
                dbus_message_iter_next(item);
            }
            return streams;
        }

        /// <summary>The PipeWire remote of the session: a file descriptor, the caller's, of OpenPipeWireRemote's reply.</summary>
        private int OpenPipeWireRemote()
        {
            var error = stackalloc byte[64];
            dbus_error_init(error);
            IntPtr message = dbus_message_new_method_call(PortalName, PortalPath, ScreenCast, "OpenPipeWireRemote");
            using (var writer = new Writer(message))
            {
                writer.ObjectPath(_session);
                writer.OpenDict();
                writer.Close();
            }
            IntPtr reply = dbus_connection_send_with_reply_and_block(_connection, message, 30000, error);
            dbus_message_unref(message);
            if (reply == IntPtr.Zero)
                throw new InvalidOperationException($"The screen capture portal opened no PipeWire remote: {ErrorMessage(error)}");
            try
            {
                var iter = stackalloc byte[IterSize];
                int fd = -1;
                if (dbus_message_iter_init(reply, iter) != 0 && dbus_message_iter_get_arg_type(iter) == TypeUnixFd)
                    dbus_message_iter_get_basic(iter, &fd);
                if (fd < 0)
                    throw new InvalidOperationException("The screen capture portal handed out no PipeWire remote");
                return fd;
            }
            finally
            {
                dbus_message_unref(reply);
            }
        }

        private static string GetString(byte* iter)
        {
            int type = dbus_message_iter_get_arg_type(iter);
            if (type != TypeString && type != TypeObjectPath)
                return null;
            IntPtr value = IntPtr.Zero;
            dbus_message_iter_get_basic(iter, &value);
            return Marshal.PtrToStringUTF8(value);
        }

        private static string ErrorMessage(byte* error)
        {
            if (dbus_error_is_set(error) == 0)
                return "no reason given";
            // DBusError: its name, then its message
            string message = Marshal.PtrToStringUTF8(*(IntPtr*)(error + IntPtr.Size));
            dbus_error_free(error);
            return message;
        }

        /// <summary>Writes a message's arguments: of the iterators of libdbus, one for each container open.</summary>
        private sealed class Writer : IDisposable
        {
            private readonly Stack<IntPtr> _iters = new Stack<IntPtr>();

            public Writer(IntPtr message)
            {
                IntPtr iter = (IntPtr)NativeMemory.AllocZeroed(IterSize);
                dbus_message_iter_init_append(message, (byte*)iter);
                _iters.Push(iter);
            }

            private byte* Current => (byte*)_iters.Peek();

            public void String(string value) => AppendString(Current, TypeString, value);

            public void ObjectPath(string value) => AppendString(Current, TypeObjectPath, value);

            /// <summary>Opens an a{sv}, a vardict: the portal's options.</summary>
            public void OpenDict() => Open(TypeArray, "{sv}");

            public void Entry(string key, string value)
            {
                BeginEntry(key, "s");
                AppendString(Current, TypeString, value);
                EndEntry();
            }

            public void Entry(string key, uint value)
            {
                BeginEntry(key, "u");
                dbus_message_iter_append_basic(Current, TypeUInt32, &value);
                EndEntry();
            }

            public void Entry(string key, bool value)
            {
                uint b = value ? 1u : 0u;
                BeginEntry(key, "b");
                dbus_message_iter_append_basic(Current, TypeBoolean, &b);
                EndEntry();
            }

            /// <summary>A dict entry's key, and its variant opened, of the signature given.</summary>
            private void BeginEntry(string key, string signature)
            {
                Open(TypeDictEntry, null);
                AppendString(Current, TypeString, key);
                Open(TypeVariant, signature);
            }

            private void EndEntry()
            {
                Close();
                Close();
            }

            private static void AppendString(byte* iter, int type, string value)
            {
                IntPtr text = Marshal.StringToCoTaskMemUTF8(value);
                try
                {
                    dbus_message_iter_append_basic(iter, type, &text);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(text);
                }
            }

            private void Open(int type, string signature)
            {
                IntPtr sub = (IntPtr)NativeMemory.AllocZeroed(IterSize);
                dbus_message_iter_open_container(Current, type, signature, (byte*)sub);
                _iters.Push(sub);
            }

            public void Close()
            {
                IntPtr sub = _iters.Pop();
                dbus_message_iter_close_container(Current, (byte*)sub);
                NativeMemory.Free((void*)sub);
            }

            public void Dispose()
            {
                while (_iters.Count > 0)
                    NativeMemory.Free((void*)_iters.Pop());
            }
        }

        public void Dispose()
        {
            if (_connection == IntPtr.Zero)
                return;
            if (_session != null)
            {
                // the session ends, and with it the stream
                IntPtr message = dbus_message_new_method_call(PortalName, _session, "org.freedesktop.portal.Session", "Close");
                dbus_connection_send(_connection, message, IntPtr.Zero);
                dbus_connection_flush(_connection);
                dbus_message_unref(message);
            }
            dbus_connection_close(_connection);
            dbus_connection_unref(_connection);
            _connection = IntPtr.Zero;
        }

        [DllImport(Lib)] private static extern void dbus_error_init(byte* error);
        [DllImport(Lib)] private static extern void dbus_error_free(byte* error);
        [DllImport(Lib)] private static extern int dbus_error_is_set(byte* error);
        [DllImport(Lib)] private static extern IntPtr dbus_bus_get_private(int type, byte* error);
        [DllImport(Lib)] private static extern IntPtr dbus_bus_get_unique_name(IntPtr connection);
        [DllImport(Lib)] private static extern void dbus_bus_add_match(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string rule, byte* error);
        [DllImport(Lib)] private static extern void dbus_connection_set_exit_on_disconnect(IntPtr connection, int exit);
        [DllImport(Lib)] private static extern IntPtr dbus_connection_send_with_reply_and_block(IntPtr connection, IntPtr message, int timeoutMs, byte* error);
        [DllImport(Lib)] private static extern int dbus_connection_send(IntPtr connection, IntPtr message, IntPtr serial);
        [DllImport(Lib)] private static extern void dbus_connection_flush(IntPtr connection);
        [DllImport(Lib)] private static extern int dbus_connection_read_write(IntPtr connection, int timeoutMs);
        [DllImport(Lib)] private static extern IntPtr dbus_connection_pop_message(IntPtr connection);
        [DllImport(Lib)] private static extern void dbus_connection_close(IntPtr connection);
        [DllImport(Lib)] private static extern void dbus_connection_unref(IntPtr connection);
        [DllImport(Lib)] private static extern IntPtr dbus_message_new_method_call([MarshalAs(UnmanagedType.LPUTF8Str)] string destination, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string method);
        [DllImport(Lib)] private static extern void dbus_message_unref(IntPtr message);
        [DllImport(Lib)] private static extern int dbus_message_is_signal(IntPtr message, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Lib)] private static extern IntPtr dbus_message_get_path(IntPtr message);
        [DllImport(Lib)] private static extern void dbus_message_iter_init_append(IntPtr message, byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_append_basic(byte* iter, int type, void* value);
        [DllImport(Lib)] private static extern int dbus_message_iter_open_container(byte* iter, int type, [MarshalAs(UnmanagedType.LPUTF8Str)] string signature, byte* sub);
        [DllImport(Lib)] private static extern int dbus_message_iter_close_container(byte* iter, byte* sub);
        [DllImport(Lib)] private static extern int dbus_message_iter_init(IntPtr message, byte* iter);
        [DllImport(Lib)] private static extern int dbus_message_iter_get_arg_type(byte* iter);
        [DllImport(Lib)] private static extern void dbus_message_iter_get_basic(byte* iter, void* value);
        [DllImport(Lib)] private static extern int dbus_message_iter_next(byte* iter);
        [DllImport(Lib)] private static extern void dbus_message_iter_recurse(byte* iter, byte* sub);
    }
}
