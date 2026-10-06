using System.Runtime.Versioning;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms
{
    /// <summary>
    /// A video transform of Media Foundation's, which can decode on the GPU: given a device, it hands out frames of the
    /// GPU's memory, as Media Foundation's samples. What is of Media Foundation alone, kept out of
    /// <see cref="IMediaVideoTransform"/> - of which a transform of another platform's is made.
    /// </summary>
    [SupportedOSPlatform("windows10.0.17763.0")]
    public interface IMediaFoundationVideoTransform : IMediaVideoTransform
    {
        /// <summary>
        /// The next frame as the transform hands it out, not copied - of the GPU's memory, decoding on a device - with its
        /// time; the caller's to let go of. Of a transform that <see cref="ProvidesSamples"/>.
        /// </summary>
        bool ProcessOutput(out IMFSample sample, out long timestamp);

        /// <summary>The device to decode on, given before <see cref="IMediaTransform.Initialize"/>; see <see cref="UsesDevice"/>.</summary>
        IMFDXGIDeviceManager DeviceManager { get; set; }

        /// <summary>Whether the transform took the <see cref="DeviceManager"/>.</summary>
        bool UsesDevice { get; }

        /// <summary>Whether the transform hands out samples of its own, rather than filling the caller's.</summary>
        bool ProvidesSamples { get; }
    }
}
