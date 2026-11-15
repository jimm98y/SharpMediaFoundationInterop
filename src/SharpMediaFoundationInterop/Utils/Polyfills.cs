#if !NETCOREAPP
// What .NET Framework and .NET Standard lack of what this library calls, beside Meziantou.Polyfill's: written here where
// its polyfill is wrong for this library, or where it has none.
using System.Runtime.InteropServices;

namespace System
{
    internal static class OperatingSystemPolyfill
    {
        extension(OperatingSystem)
        {
            /// <summary>
            /// Whether this is Windows of the version given or later: of RtlGetVersion, which tells the truth. Environment.OSVersion,
            /// which Meziantou.Polyfill's reads, says 6.2 - Windows 8 - on .NET Framework to an app whose manifest does not declare
            /// Windows 10, and so would say no to every check of 10.0.17763 of this library. The revision is not known; of one asked
            /// past 0 it says no.
            /// </summary>
            public static bool IsWindowsVersionAtLeast(int major, int minor = 0, int build = 0, int revision = 0)
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return false;
                var info = new RTL_OSVERSIONINFOW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOW>() };
                if (RtlGetVersion(ref info) != 0)
                    return false;
                var current = new Version((int)info.dwMajorVersion, (int)info.dwMinorVersion, (int)info.dwBuildNumber, 0);
                return current >= new Version(major, minor, build, revision);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RTL_OSVERSIONINFOW
        {
            public uint dwOSVersionInfoSize;
            public uint dwMajorVersion;
            public uint dwMinorVersion;
            public uint dwBuildNumber;
            public uint dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW versionInformation);
    }
}

namespace System.Numerics
{
    internal static class BitOperations
    {
        /// <summary>The count of the bits set.</summary>
        public static int PopCount(uint value)
        {
            value -= (value >> 1) & 0x55555555u;
            value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
            return (int)((((value + (value >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
        }
    }
}
#endif
