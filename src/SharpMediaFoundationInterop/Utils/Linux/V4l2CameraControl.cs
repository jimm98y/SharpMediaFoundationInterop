using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Input;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// A V4L2 camera's controls (linux/videodev2.h), of a handle of its own on its node: V4L2 lets one change them while
    /// another streams, here GStreamer. Its values are V4L2's: pan and tilt in arc seconds, exposure in 100 µs, zoom, focus
    /// and iris in the camera's own units.
    /// </summary>
    [SupportedOSPlatform("linux")]
    internal sealed unsafe class V4l2CameraControl : ICameraControl, IDisposable
    {
        private const int O_RDWR = 2, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
        // _IOWR('V', n, size): of read and write (3 << 30), the size, 'V' and the number
        private const uint VIDIOC_QUERYCTRL = 0xC0445624, VIDIOC_G_CTRL = 0xC008561B, VIDIOC_S_CTRL = 0xC008561C;
        private const uint V4L2_CTRL_FLAG_DISABLED = 0x0001;

        // V4L2_CID_CAMERA_CLASS_BASE and what follows it
        private const uint CameraClass = 0x009A0900;
        private const uint ExposureAuto = CameraClass + 1, ExposureAbsolute = CameraClass + 2, PanAbsolute = CameraClass + 8,
            TiltAbsolute = CameraClass + 9, FocusAbsolute = CameraClass + 10, FocusAuto = CameraClass + 12,
            ZoomAbsolute = CameraClass + 13, IrisAbsolute = CameraClass + 17;
        // V4L2_EXPOSURE_AUTO's menu
        private const int ExposureManual = 1, ExposureAperturePriority = 3, ExposureFullAuto = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct QueryControl
        {
            public uint Id;
            public uint Type;
            public fixed byte Name[32];
            public int Minimum;
            public int Maximum;
            public int Step;
            public int DefaultValue;
            public uint Flags;
            public fixed uint Reserved[2];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Control
        {
            public uint Id;
            public int Value;
        }

        [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc")] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, void* argument);

        private int _fd;

        private V4l2CameraControl(int fd) => _fd = fd;

        /// <summary>The controls of the node, or null where it cannot be opened.</summary>
        public static V4l2CameraControl Open(string path)
        {
            int fd = path == null ? -1 : open(path, O_RDWR | O_NONBLOCK | O_CLOEXEC);
            return fd < 0 ? null : new V4l2CameraControl(fd);
        }

        private static uint Id(CameraProperty property) => property switch
        {
            CameraProperty.Pan => PanAbsolute,
            CameraProperty.Tilt => TiltAbsolute,
            CameraProperty.Zoom => ZoomAbsolute,
            CameraProperty.Exposure => ExposureAbsolute,
            CameraProperty.Iris => IrisAbsolute,
            CameraProperty.Focus => FocusAbsolute,
            _ => 0, // V4L2 has no roll
        };

        private bool Query(uint id, out QueryControl query)
        {
            query = new QueryControl { Id = id };
            fixed (QueryControl* q = &query)
                return _fd >= 0 && id != 0 && ioctl(_fd, VIDIOC_QUERYCTRL, q) == 0 && (q->Flags & V4L2_CTRL_FLAG_DISABLED) == 0;
        }

        private bool Get(uint id, out int value)
        {
            var control = new Control { Id = id };
            bool got = _fd >= 0 && ioctl(_fd, VIDIOC_G_CTRL, &control) == 0;
            value = control.Value;
            return got;
        }

        private bool Set(uint id, int value)
        {
            var control = new Control { Id = id, Value = value };
            return _fd >= 0 && ioctl(_fd, VIDIOC_S_CTRL, &control) == 0;
        }

        public bool TryGetRange(CameraProperty property, out int min, out int max, out int step, out int defaultValue)
        {
            bool got = Query(Id(property), out var query);
            (min, max, step, defaultValue) = (query.Minimum, query.Maximum, query.Step, query.DefaultValue);
            return got;
        }

        public bool TryGet(CameraProperty property, out int value, out bool auto)
        {
            auto = false;
            if (!Get(Id(property), out value))
                return false;
            if (property == CameraProperty.Exposure && Get(ExposureAuto, out int mode))
                auto = mode != ExposureManual;
            else if (property == CameraProperty.Focus && Get(FocusAuto, out int focus))
                auto = focus != 0;
            return true;
        }

        public bool TrySet(CameraProperty property, int value, bool auto = false)
        {
            uint id = Id(property);
            if (id == 0)
                return false;
            switch (property)
            {
                case CameraProperty.Exposure:
                    // of a webcam, aperture priority is its auto most often; full auto the rest
                    if (auto)
                        return Set(ExposureAuto, ExposureAperturePriority) || Set(ExposureAuto, ExposureFullAuto);
                    return Set(ExposureAuto, ExposureManual) && Set(id, value);
                case CameraProperty.Focus:
                    if (auto)
                        return Set(FocusAuto, 1);
                    // a camera of no auto focus has no FOCUS_AUTO to turn off
                    Set(FocusAuto, 0);
                    return Set(id, value);
                default:
                    return !auto && Set(id, value);
            }
        }

        public void Dispose()
        {
            if (_fd >= 0)
                close(_fd);
            _fd = -1;
        }
    }
}
