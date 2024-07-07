using System;

namespace SharpMediaFoundation
{
    [Flags]
    public enum MftInputStatusFlags
    {
        AcceptData = 1
    }

    [Flags]
    public enum MFT_OUTPUT_DATA_BUFFERFlags : uint
    {
        None = 0x00,
        FormatChange = 0x100,
        Incomplete = 0x1000000,
    }

    public interface IMediaTransform
    {
        bool ProcessInput(byte[] data, long timestamp);
        bool ProcessOutput(ref byte[] buffer, out uint length);
    }

    public interface IVideoTransform : IMediaTransform
    {
        uint OriginalWidth { get; }
        uint OriginalHeight { get; }
        uint Width { get; }
        uint Height { get; }
    }
}
