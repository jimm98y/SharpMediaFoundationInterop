using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpMediaFoundationInterop.Wave;
using SharpMediaFoundationInterop.Transforms.AAC;
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

    using (var audioDecoder = new AACDecoder(aacTrack.ChannelCount, aacTrack.SamplingRate, AACDecoder.CreateUserData(aacTrack.AudioSpecificConfig.ToBytes()), aacTrack.ChannelConfiguration))
    {
        audioDecoder.Initialize();

        byte[] pcmBuffer = new byte[audioDecoder.OutputSize];
        using (var waveOut = new WaveOut())
        {
            waveOut.Initialize(aacTrack.SamplingRate, aacTrack.ChannelCount, 16);

            MediaSample sample;
            while ((sample = inputReader.ReadSample(aacTrack.TrackID)) != null)
            {
                IEnumerable<byte[]> audioFrames = inputReader.ParseSample(aacTrack.TrackID, sample.Data);
                foreach (var audioFrame in audioFrames)
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
        }
    }
}
