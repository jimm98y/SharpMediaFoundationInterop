using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>AAudio, the NDK's sound API of Android 8 or later: streams out to the speaker and in from the microphone.</summary>
    [SupportedOSPlatform("android26.0")]
    internal static unsafe partial class AAudio
    {
        private const string Lib = "libaaudio.so";

        public const int AAUDIO_OK = 0;
        public const int DIRECTION_OUTPUT = 0;
        public const int DIRECTION_INPUT = 1;
        public const int FORMAT_PCM_I16 = 1;
        public const int FORMAT_PCM_FLOAT = 2;
        public const int FORMAT_PCM_I24_PACKED = 3;
        public const int FORMAT_PCM_I32 = 4;
        public const int SHARING_MODE_SHARED = 1;
        public const int PERFORMANCE_MODE_NONE = 10;

        [LibraryImport(Lib)] public static partial int AAudio_createStreamBuilder(out IntPtr builder);
        [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setDirection(IntPtr builder, int direction);
        [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSampleRate(IntPtr builder, int sampleRate);
        [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setChannelCount(IntPtr builder, int channelCount);
        [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setFormat(IntPtr builder, int format);
        [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setSharingMode(IntPtr builder, int sharingMode);
        [LibraryImport(Lib)] public static partial void AAudioStreamBuilder_setPerformanceMode(IntPtr builder, int mode);
        [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_openStream(IntPtr builder, out IntPtr stream);
        [LibraryImport(Lib)] public static partial int AAudioStreamBuilder_delete(IntPtr builder);
        [LibraryImport(Lib)] public static partial int AAudioStream_requestStart(IntPtr stream);
        [LibraryImport(Lib)] public static partial int AAudioStream_requestPause(IntPtr stream);
        [LibraryImport(Lib)] public static partial int AAudioStream_requestFlush(IntPtr stream);
        [LibraryImport(Lib)] public static partial int AAudioStream_requestStop(IntPtr stream);
        [LibraryImport(Lib)] public static partial int AAudioStream_close(IntPtr stream);
        [LibraryImport(Lib)] public static partial int AAudioStream_write(IntPtr stream, void* buffer, int numFrames, long timeoutNanoseconds);
        [LibraryImport(Lib)] public static partial int AAudioStream_read(IntPtr stream, void* buffer, int numFrames, long timeoutNanoseconds);
        [LibraryImport(Lib)] public static partial long AAudioStream_getFramesWritten(IntPtr stream);
        [LibraryImport(Lib)] public static partial long AAudioStream_getFramesRead(IntPtr stream);
        [LibraryImport(Lib)] public static partial IntPtr AAudio_convertResultToText(int result);
        [LibraryImport(Lib)] public static partial int AAudioStream_waitForStateChange(IntPtr stream, int inputState, out int nextState, long timeoutNanoseconds);
        [LibraryImport(Lib)] public static partial int AAudioStream_getState(IntPtr stream);

        public const int STREAM_STATE_PAUSING = 7;
        public const int STREAM_STATE_PAUSED = 8;

        /// <summary>Pauses the stream and waits until it is paused: AAudio's pause is asked for, and plays out what is in flight first.</summary>
        public static void PauseAndWait(IntPtr stream)
        {
            AAudioStream_requestPause(stream);
            int state = AAudioStream_getState(stream);
            for (int i = 0; i < 10 && state != STREAM_STATE_PAUSED; i++)
            {
                if (AAudioStream_waitForStateChange(stream, state, out state, 20_000_000) != AAUDIO_OK)
                    break;
            }
            // the frames read are of the device's timestamps, which come a little after: until they stand still
            long read = AAudioStream_getFramesRead(stream);
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(10);
                long next = AAudioStream_getFramesRead(stream);
                if (next == read)
                    break;
                read = next;
            }
        }

        private static bool? _available;

        public static bool IsAvailable => _available ??= NativeLibrary.TryLoad(Lib, out _);

        public static string Text(int result) => Marshal.PtrToStringUTF8(AAudio_convertResultToText(result)) ?? result.ToString();

        /// <summary>A stream of the direction and PCM given, opened: its format AAudio's of the bits - 24 and 32 of Android 12 or later.</summary>
        public static IntPtr Open(int direction, uint sampleRate, uint channels, uint bitsPerSample)
        {
            int format = bitsPerSample switch
            {
                16 => FORMAT_PCM_I16,
                24 when OperatingSystem.IsAndroidVersionAtLeast(31) => FORMAT_PCM_I24_PACKED,
                32 when OperatingSystem.IsAndroidVersionAtLeast(31) => FORMAT_PCM_I32,
                _ => throw new NotSupportedException($"AAudio has no PCM of {bitsPerSample} bits here: of 16, or of Android 12, 24 and 32")
            };
            if (AAudio_createStreamBuilder(out IntPtr builder) != AAUDIO_OK)
                throw new InvalidOperationException("AAudio made no stream builder");
            try
            {
                AAudioStreamBuilder_setDirection(builder, direction);
                AAudioStreamBuilder_setSampleRate(builder, (int)sampleRate);
                AAudioStreamBuilder_setChannelCount(builder, (int)channels);
                AAudioStreamBuilder_setFormat(builder, format);
                AAudioStreamBuilder_setSharingMode(builder, SHARING_MODE_SHARED);
                AAudioStreamBuilder_setPerformanceMode(builder, PERFORMANCE_MODE_NONE);
                int result = AAudioStreamBuilder_openStream(builder, out IntPtr stream);
                if (result != AAUDIO_OK)
                    throw new InvalidOperationException(direction == DIRECTION_INPUT
                        ? $"AAudio opened no sound input: {Text(result)} - the app needs the RECORD_AUDIO permission, granted"
                        : $"AAudio opened no sound output: {Text(result)}");
                return stream;
            }
            finally
            {
                AAudioStreamBuilder_delete(builder);
            }
        }
    }
}
