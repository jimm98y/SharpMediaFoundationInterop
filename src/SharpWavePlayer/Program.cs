using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SharpMp4;
using SharpMediaFoundationInterop.Wave;
using SharpMediaFoundationInterop.Transforms.AAC;

const string sourceFileName = "frag_bunny.mp4";

using (Stream sourceFileStream = new BufferedStream(new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
{
    using (var sourceFile = await FragmentedMp4.ParseAsync(sourceFileStream))
    {
        var sourceAudioTrackBox = sourceFile.FindAudioTracks().FirstOrDefault();
        var sourceParsedMdat = await sourceFile.ParseMdatAsync();
        var sourceAudioTrackId = sourceFile.FindAudioTrackID().First();
        var sourceAudioSampleBox =
            sourceAudioTrackBox
                .GetMdia()
                .GetMinf()
                .GetStbl()
                .GetStsd()
                .Children.Single((Mp4Box x) => x is AudioSampleEntryBox) as AudioSampleEntryBox;
        var audioDescriptor = sourceAudioSampleBox.GetAudioSpecificConfigDescriptor();

        byte[] audioSpecificConfig = await audioDescriptor.ToBytes();
        uint channelCount = sourceAudioSampleBox.ChannelCount;
        int channelConfiguration = audioDescriptor.ChannelConfiguration;
        uint sampleRate = (uint)audioDescriptor.GetSamplingFrequency();
        using (var audioDecoder = new AACDecoder(channelCount, sampleRate, AACDecoder.CreateUserData(audioSpecificConfig), channelConfiguration))
        {
            audioDecoder.Initialize();

            byte[] pcmBuffer = new byte[audioDecoder.OutputSize];
            using (var waveOut = new WaveOut())
            {
                waveOut.Initialize(sampleRate, channelCount, 16);

                foreach (var audioFrame in sourceParsedMdat[sourceAudioTrackId].First())
                {
                    if (audioDecoder.ProcessInput(audioFrame, 0))
                    {
                        while (audioDecoder.ProcessOutput(ref pcmBuffer, out var pcmSize))
                        {
                            waveOut.Enqueue(pcmBuffer, pcmSize);

                            while(waveOut.QueuedFrames > 250)
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
