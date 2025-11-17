using System;

namespace SharpMediaFoundationInterop.Wave
{
    public class WaveInEventArgs : EventArgs
    {
        public byte[] Data { get; private set; }

        public WaveInEventArgs(byte[] data)
        {
            this.Data = data;
        }
    }
}
