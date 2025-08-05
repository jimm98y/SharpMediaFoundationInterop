using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpH264;
using SharpISOBMFF;
using SharpMediaFoundationInterop.Transforms.H264;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Builders;
using SharpMP4.Readers;
using SharpMP4.Tracks;

const string sourceFileName = "frag_bunny.mp4";
const string targetFileName = "frag_bunny_out.mp4";

using (Stream inputFileStream = new BufferedStream(new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
{
    var mp4 = new Container();
    mp4.Read(new IsoStream(inputFileStream));

    Mp4Reader inputReader = new Mp4Reader();
    inputReader.Parse(mp4);
    IEnumerable<ITrack> inputTracks = inputReader.GetTracks();
    H264Track inputVideoTrack = inputTracks.OfType<H264Track>().First();
    AACTrack inputAudioTrack = inputTracks.OfType<AACTrack>().First();

    var dimensions = inputVideoTrack.Sps.First().Value.CalculateDimensions();

    using (Stream output = new BufferedStream(new FileStream(targetFileName, FileMode.Create, FileAccess.Write, FileShare.Read)))
    {
        IMp4Builder outputBuilder = new Mp4Builder(new SingleStreamOutput(output));
        var targetVideoTrack = new H265Track();
        outputBuilder.AddTrack(targetVideoTrack);

        var targetAudioTrack = inputAudioTrack.Clone();
        outputBuilder.AddTrack(targetAudioTrack);

        using (var videoDecoder = new H264Decoder(dimensions.Width, dimensions.Height, inputVideoTrack.Timescale, (uint)inputVideoTrack.DefaultSampleDuration))
        {
            videoDecoder.Initialize();
            using (var videoEncoder = new H265Encoder(dimensions.Width, dimensions.Height, inputVideoTrack.Timescale, (uint)inputVideoTrack.DefaultSampleDuration))
            {
                videoEncoder.Initialize();

                var nv12Buffer = new byte[videoDecoder.OutputSize];
                var naluBuffer = new byte[videoEncoder.OutputSize];

                byte[] croppedNV12 = new byte[dimensions.Width * dimensions.Height * 3 / 2];

                var videoUnits = inputVideoTrack.GetContainerSamples();
                foreach (var unit in videoUnits)
                {
                    videoDecoder.ProcessInput(unit, 0);
                }

                Mp4Sample sample = null;
                while ((sample = inputReader.ReadSample(inputVideoTrack.TrackID)) != null)
                {
                    IEnumerable<byte[]> units = inputReader.ParseSample(inputVideoTrack.TrackID, sample.Data);
                    foreach (var sourceNALU in units)
                    {
                        if (videoDecoder.ProcessInput(sourceNALU, 0))
                        {
                            while (videoDecoder.ProcessOutput(ref nv12Buffer, out _))
                            {
                                // crop the green border from decoded H264
                                BitmapUtils.CopyNV12Bitmap(nv12Buffer, (int)videoDecoder.Width, (int)videoDecoder.Height, croppedNV12, (int)dimensions.Width, (int)dimensions.Height, false);
                                if (videoEncoder.ProcessInput(croppedNV12, 0))
                                {
                                    while (videoEncoder.ProcessOutput(ref naluBuffer, out var length))
                                    {
                                        var targetAU = AnnexBUtils.ParseNalu(naluBuffer, length);
                                        foreach (var targetNALU in targetAU)
                                        {
                                            outputBuilder.ProcessTrackSample(targetVideoTrack.TrackID, targetNALU);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                while ((sample = inputReader.ReadSample(inputAudioTrack.TrackID)) != null)
                {
                    outputBuilder.ProcessTrackSample(targetAudioTrack.TrackID, sample.Data, sample.Duration);
                }
            }
        }

        outputBuilder.FinalizeMedia();
    }
}
