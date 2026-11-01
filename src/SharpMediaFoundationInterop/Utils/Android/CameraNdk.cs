using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>The NDK's Camera2, libcamera2ndk: the device's cameras, their characteristics, and capture sessions of them.</summary>
    [SupportedOSPlatform("android26.0")]
    internal static unsafe partial class CameraNdk
    {
        private const string Lib = "libcamera2ndk.so";

        public const int ACAMERA_OK = 0;
        public const int ACAMERA_ERROR_PERMISSION_DENIED = -10013;
        public const int TEMPLATE_RECORD = 3;
        private const uint ACAMERA_LENS_FACING = 0x80005;
        private const uint ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS = 0xD000A;

        [StructLayout(LayoutKind.Sequential)]
        private struct ACameraIdList
        {
            public int NumCameras;
            public IntPtr* CameraIds;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACameraMetadataConstEntry
        {
            public uint Tag;
            public byte Type;
            public uint Count;
            public void* Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceStateCallbacks
        {
            public IntPtr Context;
            public delegate* unmanaged<IntPtr, IntPtr, void> OnDisconnected;
            public delegate* unmanaged<IntPtr, IntPtr, int, void> OnError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SessionStateCallbacks
        {
            public IntPtr Context;
            public delegate* unmanaged<IntPtr, IntPtr, void> OnClosed;
            public delegate* unmanaged<IntPtr, IntPtr, void> OnReady;
            public delegate* unmanaged<IntPtr, IntPtr, void> OnActive;
        }

        [LibraryImport(Lib)] public static partial IntPtr ACameraManager_create();
        [LibraryImport(Lib)] public static partial void ACameraManager_delete(IntPtr manager);
        [LibraryImport(Lib)] private static partial int ACameraManager_getCameraIdList(IntPtr manager, out ACameraIdList* list);
        [LibraryImport(Lib)] private static partial void ACameraManager_deleteCameraIdList(ACameraIdList* list);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int ACameraManager_getCameraCharacteristics(IntPtr manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, out IntPtr metadata);
        [LibraryImport(Lib)] private static partial void ACameraMetadata_free(IntPtr metadata);
        [LibraryImport(Lib)] private static partial int ACameraMetadata_getConstEntry(IntPtr metadata, uint tag, out ACameraMetadataConstEntry entry);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int ACameraManager_openCamera(IntPtr manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string id, DeviceStateCallbacks* callbacks, out IntPtr device);
        [LibraryImport(Lib)] public static partial int ACameraDevice_close(IntPtr device);
        [LibraryImport(Lib)] public static partial int ACameraDevice_createCaptureRequest(IntPtr device, int template, out IntPtr request);
        [LibraryImport(Lib)] public static partial void ACaptureRequest_free(IntPtr request);
        [LibraryImport(Lib)] public static partial int ACaptureRequest_addTarget(IntPtr request, IntPtr target);
        [LibraryImport(Lib)] public static partial int ACameraOutputTarget_create(IntPtr window, out IntPtr target);
        [LibraryImport(Lib)] public static partial void ACameraOutputTarget_free(IntPtr target);
        [LibraryImport(Lib)] public static partial int ACaptureSessionOutputContainer_create(out IntPtr container);
        [LibraryImport(Lib)] public static partial void ACaptureSessionOutputContainer_free(IntPtr container);
        [LibraryImport(Lib)] public static partial int ACaptureSessionOutputContainer_add(IntPtr container, IntPtr output);
        [LibraryImport(Lib)] public static partial int ACaptureSessionOutput_create(IntPtr window, out IntPtr output);
        [LibraryImport(Lib)] public static partial void ACaptureSessionOutput_free(IntPtr output);
        [LibraryImport(Lib)] public static partial int ACameraDevice_createCaptureSession(IntPtr device, IntPtr outputs, SessionStateCallbacks* callbacks, out IntPtr session);
        [LibraryImport(Lib)] public static partial int ACameraCaptureSession_setRepeatingRequest(IntPtr session, IntPtr callbacks, int numRequests, IntPtr* requests, IntPtr captureSequenceId);
        [LibraryImport(Lib)] public static partial int ACameraCaptureSession_stopRepeating(IntPtr session);
        [LibraryImport(Lib)] public static partial void ACameraCaptureSession_close(IntPtr session);

        private static bool? _available;

        public static bool IsAvailable => _available ??= NativeLibrary.TryLoad(Lib, out _);

        /// <summary>The cameras: each its id, and a name of where it faces.</summary>
        public static (string Id, string Name)[] List()
        {
            var cameras = new List<(string, string)>();
            if (!IsAvailable)
                return cameras.ToArray();
            IntPtr manager = ACameraManager_create();
            try
            {
                if (ACameraManager_getCameraIdList(manager, out ACameraIdList* list) != ACAMERA_OK)
                    return cameras.ToArray();
                try
                {
                    for (int i = 0; i < list->NumCameras; i++)
                    {
                        string id = Marshal.PtrToStringUTF8(list->CameraIds[i]);
                        cameras.Add((id, $"{Facing(manager, id)} camera ({id})"));
                    }
                }
                finally
                {
                    ACameraManager_deleteCameraIdList(list);
                }
            }
            finally
            {
                ACameraManager_delete(manager);
            }
            return cameras.ToArray();
        }

        private static string Facing(IntPtr manager, string id)
        {
            if (ACameraManager_getCameraCharacteristics(manager, id, out IntPtr metadata) != ACAMERA_OK)
                return "A";
            try
            {
                if (ACameraMetadata_getConstEntry(metadata, ACAMERA_LENS_FACING, out var entry) != ACAMERA_OK || entry.Count == 0)
                    return "A";
                return *(byte*)entry.Data switch { 0 => "Front", 1 => "Back", _ => "External" };
            }
            finally
            {
                ACameraMetadata_free(metadata);
            }
        }

        /// <summary>The widest YUV_420_888 size the camera outputs, of those the tallest; 0 by 0 where it says none.</summary>
        public static (int Width, int Height) LargestYuvSize(IntPtr manager, string id)
        {
            if (ACameraManager_getCameraCharacteristics(manager, id, out IntPtr metadata) != ACAMERA_OK)
                return (0, 0);
            try
            {
                if (ACameraMetadata_getConstEntry(metadata, ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS, out var entry) != ACAMERA_OK)
                    return (0, 0);
                // of four ints each: the format, the width, the height, and whether it is an input
                var configurations = new ReadOnlySpan<int>(entry.Data, (int)entry.Count);
                (int, int) best = (0, 0);
                for (int i = 0; i + 3 < configurations.Length; i += 4)
                {
                    if (configurations[i] != MediaNdk.AIMAGE_FORMAT_YUV_420_888 || configurations[i + 3] != 0)
                        continue;
                    int width = configurations[i + 1], height = configurations[i + 2];
                    if (width > best.Item1 || (width == best.Item1 && height > best.Item2))
                        best = (width, height);
                }
                return best;
            }
            finally
            {
                ACameraMetadata_free(metadata);
            }
        }
    }
}
