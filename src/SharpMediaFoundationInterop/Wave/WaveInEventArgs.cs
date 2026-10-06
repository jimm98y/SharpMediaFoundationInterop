using System.Runtime.Versioning;
using SharpMediaFoundationInterop.Devices;

namespace SharpMediaFoundationInterop.Wave
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class WaveInEventArgs : AudioInputEventArgs
    {
        public WaveInEventArgs(byte[] data)
            : base(data)
        { }
    }
}
