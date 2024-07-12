
using SharpMp4;
using SharpMediaFoundation;
using SharpMediaFoundation.H264;
using SharpMediaFoundation.H265;

const string sourceFileName = "frag_bunny.mp4";
const string targetFileName = "frag_bunny_out.mp4";

using (Stream sourceFileStream = new BufferedStream(new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
{
    using (var sourceFile = await FragmentedMp4.ParseAsync(sourceFileStream))
    {
        var sourceVideoTrackBox = sourceFile.FindVideoTracks().FirstOrDefault();
        var sourceParsedMdat = await sourceFile.ParseMdatAsync();
        var sourceVideoTrackId = sourceFile.FindVideoTrackID().First();
        var sourceVisualSampleBox =
            sourceVideoTrackBox
                .GetMdia()
                .GetMinf()
                .GetStbl()
                .GetStsd()
                .Children.Single((Mp4Box x) => x is VisualSampleEntryBox) as VisualSampleEntryBox;
        var sourceOriginalWidth = sourceVisualSampleBox.Width;
        var sourceOriginalHeight = sourceVisualSampleBox.Height;
        var sourceFpsNom = sourceFile.CalculateTimescale(sourceVideoTrackBox);
        var sourceFpsDenom = sourceFile.CalculateSampleDuration(sourceVideoTrackBox);

        using (Stream targetFileStream = new BufferedStream(new FileStream(targetFileName, FileMode.Create, FileAccess.Write, FileShare.Read)))
        {
            using (FragmentedMp4Builder targetFile = new FragmentedMp4Builder(new SingleStreamOutput(targetFileStream)))
            {
                var targetVideoTrack = new H265Track();
                targetFile.AddTrack(targetVideoTrack);

                if (sourceVisualSampleBox.Children.FirstOrDefault(x => x is AvcConfigurationBox) != null)
                {
                    var videoDecoder = new H264Decoder(sourceOriginalWidth, sourceOriginalHeight, sourceFpsNom, sourceFpsDenom);
                    var videoEncoder = new H265Encoder(videoDecoder.Width, videoDecoder.Height, sourceFpsNom, sourceFpsDenom);
                    var nv12Buffer = new byte[videoDecoder.OutputSize];
                    var naluBuffer = new byte[videoDecoder.OutputSize];

                    foreach (var sourceAU in sourceParsedMdat[sourceVideoTrackId])
                    {
                        foreach (var sourceNALU in sourceAU)
                        {
                            if (videoDecoder.ProcessInput(sourceNALU, 0))
                            {
                                while (videoDecoder.ProcessOutput(ref nv12Buffer, out _))
                                {
                                    // TODO: crop the green border from decoded H264
                                    if (videoEncoder.ProcessInput(nv12Buffer, 0))
                                    {
                                        while (videoEncoder.ProcessOutput(ref naluBuffer, out var length))
                                        {
                                            var targetAU = AnnexBUtils.ParseNalu(naluBuffer, length);
                                            foreach (var targetNALU in targetAU)
                                            {
                                                await targetVideoTrack.ProcessSampleAsync(targetNALU);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                await targetVideoTrack.FlushAsync();
                await targetFile.FlushAsync();
            }
        }
    }
}
