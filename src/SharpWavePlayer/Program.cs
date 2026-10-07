using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpMediaFoundationInterop;
using SharpMediaFoundationInterop.Codecs;
using SharpISOBMFF;
using SharpMP4.Readers;
using System.Collections.Generic;
using SharpMP4.Tracks;
using SharpISOBMFF.Extensions;

const string sourceFileName = "frag_bunny.mp4";

using (Stream inputFileStream = new BufferedStream(new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
{
    var mp4 = new Container();
    mp4.Read(new IsoStream(inputFileStream));

    VideoReader inputReader = new VideoReader();
    inputReader.Parse(mp4);
    IEnumerable<ITrack> inputTracks = inputReader.GetTracks();
    AACTrack aacTrack = inputTracks.OfType<AACTrack>().First();

    var options = new AudioDecoderOptions
    {
        Channels = aacTrack.ChannelCount,
        SampleRate = aacTrack.SamplingRate,
        Config = aacTrack.AudioSpecificConfig.ToBytes(),
    };
    using (var audioDecoder = MediaCodecs.CreateAudioDecoder(AudioCodec.AAC, options))
    {
        audioDecoder.Initialize();

        byte[] pcmBuffer = new byte[audioDecoder.OutputSize];
        using (var waveOut = MediaDevices.CreateAudioOutput(aacTrack.SamplingRate, aacTrack.ChannelCount, 16))
        {
            waveOut.Initialize();

            MediaSample sample;
            while ((sample = inputReader.ReadSample(aacTrack.TrackID)) != null)
            {
                foreach (var audioFrame in inputReader.ParseSample(aacTrack.TrackID, sample.Data))
                {
                    if (audioDecoder.ProcessInput(audioFrame, 0))
                    {
                        while (audioDecoder.ProcessOutput(ref pcmBuffer, out var pcmSize))
                        {
                            waveOut.Enqueue(pcmBuffer, pcmSize);

                            while (waveOut.QueuedFrames > 250)
                            {
                                await Task.Delay(50);
                            }
                        }
                    }
                }
            }

            // what the decoder still holds, then what is queued played out before the device is closed
            audioDecoder.BeginDrain();
            while (audioDecoder.ProcessOutput(ref pcmBuffer, out var pcmSize))
                waveOut.Enqueue(pcmBuffer, pcmSize);
            audioDecoder.EndDrain();
            while (waveOut.QueuedFrames > 0)
                await Task.Delay(50);
        }
    }
}
