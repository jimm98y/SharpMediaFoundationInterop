using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpH264;
using SharpISOBMFF;
using SharpMediaFoundationInterop.Codecs;
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

    VideoReader inputReader = new VideoReader();
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

        var decoderOptions = new VideoDecoderOptions
        {
            Width = dimensions.Width,
            Height = dimensions.Height,
            FpsNom = inputVideoTrack.Timescale,
            FpsDenom = (uint)inputVideoTrack.DefaultSampleDuration,
        };
        using (var videoDecoder = MediaCodecs.CreateVideoDecoder(VideoCodec.H264, decoderOptions))
        {
            videoDecoder.Initialize();
            var encoderOptions = new VideoEncoderOptions
            {
                Width = dimensions.Width,
                Height = dimensions.Height,
                FpsNom = inputVideoTrack.Timescale,
                FpsDenom = (uint)inputVideoTrack.DefaultSampleDuration,
            };
            using (var videoEncoder = MediaCodecs.CreateVideoEncoder(VideoCodec.H265, encoderOptions))
            {
                videoEncoder.Initialize();

                var nv12Buffer = new byte[videoDecoder.OutputSize];
                var naluBuffer = new byte[videoEncoder.OutputSize];

                byte[] croppedNV12 = new byte[dimensions.Width * dimensions.Height * 3 / 2];

                void Write()
                {
                    while (videoEncoder.ProcessOutput(ref naluBuffer, out var length))
                    {
                        // the encoder's access unit as it hands it out, start codes and all, without a copy
                        outputBuilder.ProcessAnnexBTrackSample(targetVideoTrack.TrackID, new ArraySegment<byte>(naluBuffer, 0, (int)length));
                    }
                }

                void Encode()
                {
                    // each frame with the time of the sample it came of: the decoder puts its frames in the order they are shown by it
                    while (videoDecoder.ProcessOutput(ref nv12Buffer, out _, out long frameTime))
                    {
                        // crop the green border from decoded H264
                        BitmapUtils.CopyNV12Bitmap(nv12Buffer, (int)videoDecoder.Width, (int)videoDecoder.Height, croppedNV12, (int)dimensions.Width, (int)dimensions.Height, false);
                        if (videoEncoder.ProcessInput(croppedNV12, frameTime))
                            Write();
                    }
                }

                var videoUnits = inputVideoTrack.GetContainerSamples();
                foreach (var unit in videoUnits)
                {
                    videoDecoder.ProcessInput(unit, 0);
                }

                MediaSample sample = null;
                while ((sample = inputReader.ReadSample(inputVideoTrack.TrackID)) != null)
                {
                    long time = MediaUtils.ToTicks(sample.PTS, inputVideoTrack.Timescale);
                    foreach (var sourceNALU in inputReader.ParseSample(inputVideoTrack.TrackID, sample.Data))
                    {
                        if (videoDecoder.ProcessInput(sourceNALU, time))
                            Encode();
                    }
                }

                // the frames the decoder, then the encoder, still hold
                videoDecoder.BeginDrain();
                Encode();
                videoDecoder.EndDrain();
                videoEncoder.BeginDrain();
                Write();
                videoEncoder.EndDrain();

                while ((sample = inputReader.ReadSample(inputAudioTrack.TrackID)) != null)
                {
                    outputBuilder.ProcessTrackSample(targetAudioTrack.TrackID, sample.Data, sample.Duration);
                }
            }
        }

        outputBuilder.FinalizeMedia();
    }
}
