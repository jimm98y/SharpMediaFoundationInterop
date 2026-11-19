using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Input;
using static SharpMediaFoundationInterop.Utils.AppleNative;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// A USB camera's controls on macOS, of USB Video Class requests to its camera terminal, which AVFoundation does not
    /// reach on a Mac: pan and tilt in arc seconds, roll in degrees, exposure in 100 µs, zoom, focus and iris in the
    /// camera's own units - UVC's, as V4L2's on Linux are. The camera is found of its AVFoundation id, which of a USB
    /// camera is 0x, then its location id, vendor and product in hex; its requests go to its default pipe, of IOKit's
    /// IOUSBDeviceInterface, the camera left open to AVFoundation. Opened on macOS alone: of the platforms of the capture it
    /// is of, iOS has no USB cameras to open.
    /// </summary>
    [SupportedOSPlatform("macos11.0")]
    [SupportedOSPlatform("ios14.0")]
    internal sealed unsafe class UvcCameraControl : ICameraControl, IDisposable
    {
        private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int kCFNumberSInt32Type = 3;

        // the UVC requests (UVC 1.1, A.8), of a class request to an interface
        private const byte ToHost = 0xA1, ToDevice = 0x21;
        private const byte SET_CUR = 0x01, GET_CUR = 0x81, GET_MIN = 0x82, GET_MAX = 0x83, GET_RES = 0x84, GET_DEF = 0x87;
        // the camera terminal's control selectors (A.9.4)
        private const byte CT_AE_MODE = 0x02, CT_EXPOSURE_TIME_ABSOLUTE = 0x04, CT_FOCUS_ABSOLUTE = 0x06, CT_FOCUS_AUTO = 0x08,
            CT_IRIS_ABSOLUTE = 0x09, CT_ZOOM_ABSOLUTE = 0x0B, CT_PANTILT_ABSOLUTE = 0x0D, CT_ROLL_ABSOLUTE = 0x0F;
        // CT_AE_MODE's bits
        private const byte AeManual = 1, AeAuto = 2, AeAperturePriority = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct UuidBytes
        {
            public ulong Low, High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceRequestBlock
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
            public void* Data;
            public uint LengthDone;
        }

        [DllImport(IOKitLib)] private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(IOKitLib)] private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);
        [DllImport(IOKitLib)] private static extern uint IOIteratorNext(uint iterator);
        [DllImport(IOKitLib)] private static extern int IOObjectRelease(uint obj);
        [DllImport(IOKitLib)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
        [DllImport(IOKitLib)] private static extern int IOCreatePlugInInterfaceForService(uint service, IntPtr pluginType, IntPtr interfaceType, out IntPtr** plugIn, out int score);
        [DllImport(CoreFoundationLib)] private static extern byte CFNumberGetValue(IntPtr number, int type, void* value);
        [DllImport(CoreFoundationLib)] private static extern IntPtr CFUUIDGetConstantUUIDWithBytes(IntPtr allocator,
            byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7, byte b8, byte b9, byte b10, byte b11, byte b12, byte b13, byte b14, byte b15);

        // kIOUSBDeviceUserClientTypeID, kIOCFPlugInInterfaceID and kIOUSBDeviceInterfaceID of IOUSBLib.h and IOCFPlugIn.h
        private static readonly byte[] DeviceUserClient = Convert.FromHexString("9DC7B7809EC011D4A54F000A27052861");
        private static readonly byte[] PlugInInterface = Convert.FromHexString("C244E858109C11D491D40050E4C6426F");
        private static readonly byte[] DeviceInterface = Convert.FromHexString("5C8187D09EF311D48B45000A27052861");

        // IOUSBDeviceInterface: its vtable's slots, after its reserved first one and IUnknown's three
        private const int ReleaseSlot = 3, GetConfigurationDescriptorPtrSlot = 21, DeviceRequestSlot = 26;

        private IntPtr** _device;
        private readonly byte _terminal, _interface;
        private readonly uint _controls;

        private UvcCameraControl(IntPtr** device, byte terminal, byte interfaceNumber, uint controls)
        {
            _device = device;
            _terminal = terminal;
            _interface = interfaceNumber;
            _controls = controls;
        }

        /// <summary>The controls of the USB camera of the AVFoundation id given; null of a camera that is not one, or has none.</summary>
        public static UvcCameraControl Open(string uniqueID)
        {
            // 0x, then 8 hex digits of its location, 4 of its vendor, 4 of its product
            if (uniqueID == null || uniqueID.Length != 18 || !uniqueID.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(uniqueID.AsSpan(2, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint location))
                return null;
            try
            {
                IntPtr** device = OpenDevice(location);
                if (device == null)
                    return null;
                if (FindCameraTerminal(device, out byte terminal, out byte interfaceNumber, out uint controls))
                    return new UvcCameraControl(device, terminal, interfaceNumber, controls);
                Release(device);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            return null;
        }

        private static IntPtr Uuid(byte[] b) =>
            CFUUIDGetConstantUUIDWithBytes(IntPtr.Zero, b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);

        /// <summary>The device interface of the USB device at the location, the caller's to release; null of none.</summary>
        private static IntPtr** OpenDevice(uint location)
        {
            if (IOServiceGetMatchingServices(0, IOServiceMatching("IOUSBHostDevice"), out uint iterator) != 0)
                return null;
            IntPtr key = CreateString("locationID");
            try
            {
                for (uint service; (service = IOIteratorNext(iterator)) != 0; IOObjectRelease(service))
                {
                    IntPtr number = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
                    if (number == IntPtr.Zero)
                        continue;
                    uint found = 0;
                    CFNumberGetValue(number, kCFNumberSInt32Type, &found);
                    CFRelease(number);
                    if (found != location)
                        continue;

                    int status = IOCreatePlugInInterfaceForService(service, Uuid(DeviceUserClient), Uuid(PlugInInterface), out IntPtr** plugIn, out _);
                    IOObjectRelease(service);
                    if (status != 0 || plugIn == null)
                        return null;
                    // QueryInterface, of the interface's UUID as bytes, by value
                    IntPtr** device = null;
                    fixed (byte* id = DeviceInterface)
                        ((delegate* unmanaged<IntPtr**, UuidBytes, IntPtr***, int>)(*plugIn)[1])(plugIn, *(UuidBytes*)id, &device);
                    ((delegate* unmanaged<IntPtr**, uint>)(*plugIn)[ReleaseSlot])(plugIn);
                    return device;
                }
                return null;
            }
            finally
            {
                CFRelease(key);
                IOObjectRelease(iterator);
            }
        }

        private static void Release(IntPtr** device) => ((delegate* unmanaged<IntPtr**, uint>)(*device)[ReleaseSlot])(device);

        /// <summary>
        /// Of the configuration descriptor, the Video Control interface (class 0x0E, subclass 1) and its camera terminal - an
        /// input terminal of type 0x0201 - and the controls it says it has (UVC 1.1, 3.7.2.3).
        /// </summary>
        private static bool FindCameraTerminal(IntPtr** device, out byte terminal, out byte interfaceNumber, out uint controls)
        {
            (terminal, interfaceNumber, controls) = (0, 0, 0);
            byte* descriptor = null;
            if (((delegate* unmanaged<IntPtr**, byte, byte**, int>)(*device)[GetConfigurationDescriptorPtrSlot])(device, 0, &descriptor) != 0 || descriptor == null)
                return false;
            int total = descriptor[2] | descriptor[3] << 8;
            bool videoControl = false;
            for (int i = 0; i + 2 <= total && descriptor[i] >= 2;)
            {
                byte length = descriptor[i], type = descriptor[i + 1];
                if (i + length > total)
                    break;
                byte* d = descriptor + i;
                if (type == 0x04 && length >= 9)
                {
                    videoControl = d[5] == 0x0E && d[6] == 0x01;
                    if (videoControl)
                        interfaceNumber = d[2];
                }
                else if (videoControl && type == 0x24 && length >= 15 && d[2] == 0x02 && (d[4] | d[5] << 8) == 0x0201)
                {
                    terminal = d[3];
                    int size = d[14];
                    for (int b = 0; b < size && b < 4 && 15 + b < length; b++)
                        controls |= (uint)d[15 + b] << (8 * b);
                    return true;
                }
                i += length;
            }
            return false;
        }

        private bool Request(byte type, byte request, byte selector, void* data, ushort length)
        {
            if (_device == null)
                return false;
            var block = new DeviceRequestBlock
            {
                RequestType = type,
                Request = request,
                Value = (ushort)(selector << 8),
                Index = (ushort)(_terminal << 8 | _interface),
                Length = length,
                Data = data,
            };
            return ((delegate* unmanaged<IntPtr**, DeviceRequestBlock*, int>)(*_device)[DeviceRequestSlot])(_device, &block) == 0;
        }

        // a property's selector, its size, where in it the value is and how wide, signed or not, and its bit of bmControls
        private static bool Layout(CameraProperty property, out byte selector, out ushort size, out int offset, out int width, out bool signed, out int bit)
        {
            (selector, size, offset, width, signed, bit) = property switch
            {
                CameraProperty.Exposure => (CT_EXPOSURE_TIME_ABSOLUTE, (ushort)4, 0, 4, false, 3),
                CameraProperty.Focus => (CT_FOCUS_ABSOLUTE, (ushort)2, 0, 2, false, 5),
                CameraProperty.Iris => (CT_IRIS_ABSOLUTE, (ushort)2, 0, 2, false, 7),
                CameraProperty.Zoom => (CT_ZOOM_ABSOLUTE, (ushort)2, 0, 2, false, 9),
                CameraProperty.Pan => (CT_PANTILT_ABSOLUTE, (ushort)8, 0, 4, true, 11),
                CameraProperty.Tilt => (CT_PANTILT_ABSOLUTE, (ushort)8, 4, 4, true, 11),
                CameraProperty.Roll => (CT_ROLL_ABSOLUTE, (ushort)2, 0, 2, true, 13),
                _ => ((byte)0, (ushort)0, 0, 0, false, -1),
            };
            return bit >= 0;
        }

        private static int Read(byte* data, int offset, int width, bool signed) => width switch
        {
            4 => *(int*)(data + offset),
            2 => signed ? *(short*)(data + offset) : *(ushort*)(data + offset),
            _ => data[offset],
        };

        private bool Has(CameraProperty property) => Layout(property, out _, out _, out _, out _, out _, out int bit) && (_controls & (1u << bit)) != 0;

        private bool Get(byte request, CameraProperty property, out int value)
        {
            value = 0;
            if (!Has(property))
                return false;
            Layout(property, out byte selector, out ushort size, out int offset, out int width, out bool signed, out _);
            byte* data = stackalloc byte[8];
            if (!Request(ToHost, request, selector, data, size))
                return false;
            value = Read(data, offset, width, signed);
            return true;
        }

        public bool TryGetRange(CameraProperty property, out int min, out int max, out int step, out int defaultValue)
        {
            (min, max, step, defaultValue) = (0, 0, 0, 0);
            return Get(GET_MIN, property, out min) && Get(GET_MAX, property, out max) && Get(GET_RES, property, out step)
                && Get(GET_DEF, property, out defaultValue);
        }

        public bool TryGet(CameraProperty property, out int value, out bool auto)
        {
            auto = false;
            if (!Get(GET_CUR, property, out value))
                return false;
            byte mode = 0;
            if (property == CameraProperty.Exposure && Request(ToHost, GET_CUR, CT_AE_MODE, &mode, 1))
                auto = mode != AeManual;
            else if (property == CameraProperty.Focus && (_controls & (1u << 17)) != 0 && Request(ToHost, GET_CUR, CT_FOCUS_AUTO, &mode, 1))
                auto = mode != 0;
            return true;
        }

        public bool TrySet(CameraProperty property, int value, bool auto = false)
        {
            if (!Has(property))
                return false;
            Layout(property, out byte selector, out ushort size, out int offset, out int width, out _, out _);
            byte mode;
            switch (property)
            {
                case CameraProperty.Exposure:
                    if (auto)
                    {
                        // of a webcam, aperture priority is its auto most often; full auto the rest
                        mode = AeAperturePriority;
                        if (Request(ToDevice, SET_CUR, CT_AE_MODE, &mode, 1))
                            return true;
                        mode = AeAuto;
                        return Request(ToDevice, SET_CUR, CT_AE_MODE, &mode, 1);
                    }
                    mode = AeManual;
                    Request(ToDevice, SET_CUR, CT_AE_MODE, &mode, 1);
                    break;
                case CameraProperty.Focus:
                    mode = auto ? (byte)1 : (byte)0;
                    if ((_controls & (1u << 17)) != 0 && !Request(ToDevice, SET_CUR, CT_FOCUS_AUTO, &mode, 1) && auto)
                        return false;
                    if (auto)
                        return (_controls & (1u << 17)) != 0;
                    break;
                default:
                    if (auto)
                        return false;
                    break;
            }
            byte* data = stackalloc byte[8];
            // pan and tilt are one control: the other's value as it is
            if (size > width && !Request(ToHost, GET_CUR, selector, data, size))
                return false;
            if (width == 4)
                *(int*)(data + offset) = value;
            else
                *(short*)(data + offset) = (short)value;
            return Request(ToDevice, SET_CUR, selector, data, size);
        }

        public void Dispose()
        {
            if (_device != null)
                Release(_device);
            _device = null;
        }
    }
}
