using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Utils
{
    /// <summary>
    /// Media Foundation's objects of each frame - a sample, a buffer, a buffer of a sample - each an object of its own,
    /// let go of with <see cref="ComObjects.Release"/> as soon as it is done with. The generated interop's are objects
    /// cached of their pointer, let go of only as they are collected: of a frame of megabytes, of each frame, too late.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public static unsafe partial class MediaObjects
    {
        /// <summary>A sample of no buffer.</summary>
        public static IMFSample CreateSample()
        {
            IMFSample_unmanaged* sample;
            MediaUtils.Check(CreateSampleRaw(&sample));
            return ComObjects.Take<IMFSample>(sample);
        }

        /// <summary>A buffer of memory of so many bytes at most.</summary>
        public static IMFMediaBuffer CreateMemoryBuffer(uint maxLength)
        {
            IMFMediaBuffer_unmanaged* buffer;
            MediaUtils.Check(CreateMemoryBufferRaw(maxLength, &buffer));
            return ComObjects.Take<IMFMediaBuffer>(buffer);
        }

        /// <summary>A buffer of a sample, of its index.</summary>
        public static IMFMediaBuffer GetBuffer(IMFSample sample, uint index)
        {
            var unmanaged = (IMFSample_unmanaged*)ComObjects.PointerOf(sample);
            try
            {
                unmanaged->GetBufferByIndex(index, out IMFMediaBuffer_unmanaged* buffer);
                return ComObjects.Take<IMFMediaBuffer>(buffer);
            }
            finally
            {
                unmanaged->Release();
            }
        }

        /// <summary>The bytes of a sample's buffers in one: its own buffer, of a sample of one.</summary>
        public static IMFMediaBuffer GetContiguousBuffer(IMFSample sample)
        {
            var unmanaged = (IMFSample_unmanaged*)ComObjects.PointerOf(sample);
            try
            {
                unmanaged->ConvertToContiguousBuffer(out IMFMediaBuffer_unmanaged* buffer);
                return ComObjects.Take<IMFMediaBuffer>(buffer);
            }
            finally
            {
                unmanaged->Release();
            }
        }

        /// <summary>
        /// A source reader of a media source - a camera's - let go of as soon as it is done with: one held on holds the
        /// source, and the camera, on.
        /// </summary>
        public static IMFSourceReader CreateSourceReader(IMFMediaSource source, IMFAttributes attributes)
        {
            void* sourcePointer = ComObjects.PointerOf(source);
            void* attributesPointer = ComObjects.PointerOf(attributes);
            try
            {
                void* reader;
                MediaUtils.Check(CreateSourceReaderRaw(sourcePointer, attributesPointer, &reader));
                return ComObjects.Take<IMFSourceReader>(reader);
            }
            finally
            {
                if (attributesPointer != null)
                    Marshal.Release((nint)attributesPointer);
                Marshal.Release((nint)sourcePointer);
            }
        }

        [LibraryImport("mfreadwrite.dll", EntryPoint = "MFCreateSourceReaderFromMediaSource")]
        private static partial HRESULT CreateSourceReaderRaw(void* source, void* attributes, void** reader);

        [LibraryImport("mfplat.dll", EntryPoint = "MFCreateSample")]
        private static partial HRESULT CreateSampleRaw(IMFSample_unmanaged** sample);

        [LibraryImport("mfplat.dll", EntryPoint = "MFCreateMemoryBuffer")]
        private static partial HRESULT CreateMemoryBufferRaw(uint maxLength, IMFMediaBuffer_unmanaged** buffer);
    }
}
