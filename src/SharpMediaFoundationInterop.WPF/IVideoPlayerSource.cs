using System;

namespace SharpMediaFoundationInterop.WPF
{
    public interface IVideoPlayerSource : IDisposable
    {
        // Video stream info
        bool HasVideo { get; }
        uint VideoWidth { get; }
        uint VideoHeight { get; }
        uint OriginalVideoWidth { get; }
        uint OriginalVideoHeight { get; }
        uint FpsNom { get; }
        uint FpsDenom { get; }
        string VideoCodec { get; }  // "H264", "H265", "AV1"

        // Audio stream info
        bool HasAudio { get; }
        uint AudioChannels { get; }
        uint AudioSampleRate { get; }
        string AudioCodec { get; }  // "AAC", "OPUS"
        byte[] AACUserData { get; }
        int AudioChannelConfiguration { get; }

        // True if arbitrary seeking is supported (seeking to timestamp == 0 is always supported).
        bool CanSeek { get; }

        // Total duration in 100-nanosecond units; 0 if unknown.
        long Duration { get; }

        void Initialize();

        // Seek to a position in 100-nanosecond units. Returns false if the seek failed.
        bool Seek(long timestamp);

        // Read next compressed video unit (NALU/OBU) into buffer. Returns false at EOS.
        // timestamp is in 100-nanosecond units.
        bool ReadNextVideoUnit(ref byte[] buffer, out uint length, out long timestamp);

        // Read next compressed audio unit into buffer. Returns false at EOS.
        bool ReadNextAudioUnit(ref byte[] buffer, out uint length, out long timestamp);
    }
}
