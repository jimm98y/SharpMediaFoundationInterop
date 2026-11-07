using System;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A frame as a decoder handed it out, not copied: a texture of the GPU's where it decodes on a device - see
    /// <see cref="IGpuVideoTransform"/> - else of the CPU's memory. Holds what the decoder made the frame of, given back
    /// when it is disposed of: the caller's to dispose of as soon as it is done with it.
    /// </summary>
    public abstract class DecodedFrame : IDisposable
    {
        private bool _disposed;

        private protected DecodedFrame()
        {
        }

        /// <summary>Whether the picture is in a texture of the GPU's, <see cref="Texture"/>; else it is in the CPU's memory.</summary>
        public abstract bool IsOnGpu { get; }

        /// <summary>
        /// The texture the picture is in, where it <see cref="IsOnGpu"/>: on Windows an <c>ID3D11Texture2D</c> pointer, of the
        /// device the decoder was given. Zero otherwise. The frame's, while it is not disposed of: whoever keeps it longer
        /// takes a reference of their own.
        /// </summary>
        public abstract IntPtr Texture { get; }

        /// <summary>Which of the <see cref="Texture"/>'s array slices the picture is: a decoder decodes into one array of them.</summary>
        public abstract int TextureIndex { get; }

        /// <summary>
        /// The picture copied into <paramref name="buffer"/>, of the decoder's output format, wherever it is; false where it
        /// does not fit. Of a frame on the GPU, a copy out of it, which costs what not copying saved.
        /// </summary>
        public bool CopyTo(byte[] buffer, out uint length)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DecodedFrame));
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            return CopyToCore(buffer, out length);
        }

        private protected abstract bool CopyToCore(byte[] buffer, out uint length);

        /// <summary>Gives the frame back to the decoder that made it.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Release(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~DecodedFrame()
        {
            // one not disposed of still lets go of its texture, which the decoder waits for; its wrappers of COM objects let
            // go of what they hold as they are collected, on threads of their own
            if (!_disposed)
                Release(disposing: false);
        }

        /// <summary>Lets go of the native pointers, always, and of the managed wrappers too where <paramref name="disposing"/>.</summary>
        private protected abstract void Release(bool disposing);
    }
}
