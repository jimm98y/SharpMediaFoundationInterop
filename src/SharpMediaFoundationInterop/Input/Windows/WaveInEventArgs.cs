using System.Runtime.Versioning;

namespace SharpMediaFoundationInterop.Input
{
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class WaveInEventArgs : AudioInputEventArgs
    {
        public WaveInEventArgs(byte[] data)
            : base(data)
        { }
    }
}
