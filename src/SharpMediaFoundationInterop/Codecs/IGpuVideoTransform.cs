using System;

namespace SharpMediaFoundationInterop.Codecs
{
    /// <summary>
    /// A video transform that can decode on the GPU: given a device of the caller's, it hands out its frames as it decoded
    /// them, in the GPU's memory, rather than copied into the caller's buffer. Of the platform's own graphics API, but of no
    /// type of it: what is native is a pointer.
    /// </summary>
    public interface IGpuVideoTransform : IMediaVideoTransform
    {
        /// <summary>
        /// The device to decode on, given before <see cref="IMediaTransform.Initialize"/>: on Windows an <c>ID3D11Device</c>
        /// pointer, of a device made for video and protected for more than one thread at a time. The transform takes a
        /// reference of its own while it uses it; zero, the default, decodes into the CPU's memory. See
        /// <see cref="UsesDevice"/>.
        /// </summary>
        IntPtr GpuDevice { get; set; }

        /// <summary>Whether the transform took the <see cref="GpuDevice"/>: some cannot use one.</summary>
        bool UsesDevice { get; }

        /// <summary>
        /// Whether the transform hands out frames of its own, through <see cref="ProcessOutput(out DecodedFrame, out long)"/>,
        /// rather than filling the caller's buffer - as one decoding on a device does.
        /// </summary>
        bool ProvidesSamples { get; }

        /// <summary>
        /// The next frame as the transform hands it out, not copied, with its time: of the GPU's memory where the transform
        /// decodes on the <see cref="GpuDevice"/>, though it may fall back to the CPU's. The caller's to dispose of as soon as
        /// it is done with it: a decoder has a few frames only, and waits for one held. Of a transform that
        /// <see cref="ProvidesSamples"/>.
        /// </summary>
        bool ProcessOutput(out DecodedFrame frame, out long timestamp);
    }
}
