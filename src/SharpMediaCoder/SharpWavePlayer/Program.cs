using SharpMp4;
using System.IO;
using System.Linq;
using SharpMediaFoundation.AAC;
using System.Threading.Tasks;
using SharpMediaFoundation.Output;

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
        uint channels = (uint)audioDescriptor.ChannelConfiguration;
        uint sampleRate = (uint)audioDescriptor.GetSamplingFrequency();
        using (var audioDecoder = new AACDecoder(channels, sampleRate, AACDecoder.CreateUserData(audioSpecificConfig)))
        {
            audioDecoder.Initialize();

            byte[] pcmBuffer = new byte[audioDecoder.OutputSize];
            using (var waveOut = new WaveOut())
            {
                waveOut.Initialize(sampleRate, channels, 16);

                foreach (var sourceAudioFrame in sourceParsedMdat[sourceAudioTrackId])
                {
                    foreach (var audioFrame in sourceAudioFrame)
                    {
                        if (audioDecoder.ProcessInput(audioFrame, 0))
                        {
                            while (audioDecoder.ProcessOutput(ref pcmBuffer, out var pcmSize))
                            {
                                waveOut.Play(pcmBuffer, pcmSize);

                                while(waveOut.QueuedFrames > 10)
                                {
                                    await Task.Delay(10);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
