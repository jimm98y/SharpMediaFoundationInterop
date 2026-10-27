using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SharpMediaFoundationInterop;
using SharpMediaFoundationInterop.Codecs;
using SharpMP4.Builders;
using SharpMP4.Tracks;

const string targetFileName = "webcam.mp4";
const uint fpsNom = 24000;
const uint fpsDenom = 1001;
// how long to record, in seconds, as the first argument; or, of none, until a key is pressed
double seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : double.PositiveInfinity;
Stopwatch stopwatch = new Stopwatch();

using (Stream output = new BufferedStream(new FileStream(targetFileName, FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    IMp4Builder outputBuilder = new Mp4Builder(new SingleStreamOutput(output));

    var targetVideoTrack = new H265Track();
    outputBuilder.AddTrack(targetVideoTrack);

    using (var camera = MediaDevices.CreateCameraCapture())
    {
        camera.Initialize();

        var encoderOptions = new VideoEncoderOptions
        {
            Width = camera.Width,
            Height = camera.Height,
            FpsNom = fpsNom,
            FpsDenom = fpsDenom,
        };
        using (var videoEncoder = MediaCodecs.CreateVideoEncoder(VideoCodec.H265, encoderOptions))
        {
            videoEncoder.Initialize();

            // to NV12, of what the camera hands out: as it is where it is NV12 already, as a Mac's camera's frames are, and
            // else of the system's color converter - RGB bottom-up, as a screen capture hands it out by default
            bool converts = camera.OutputFormat != videoEncoder.InputFormat;
            var converterOptions = new ColorConverterOptions { Width = camera.Width, Height = camera.Height };
            if (converts && !MediaCodecs.CanConvert(camera.OutputFormat, videoEncoder.InputFormat, converterOptions))
                throw new NotSupportedException($"No conversion of {camera.OutputFormat} to NV12 here");
            IMediaVideoTransform colorConverter = converts
                ? MediaCodecs.CreateColorConverter(camera.OutputFormat, videoEncoder.InputFormat, converterOptions)
                : null;

            using (colorConverter)
            {
                colorConverter?.Initialize();

                var frameBuffer = new byte[camera.OutputSize];
                var nv12Buffer = new byte[colorConverter?.OutputSize ?? videoEncoder.Width * videoEncoder.Height * 3 / 2];
                var naluBuffer = new byte[videoEncoder.OutputSize];

                void Write()
                {
                    while (videoEncoder.ProcessOutput(ref naluBuffer, out var length))
                    {
                        // the encoder's access unit as it hands it out, start codes and all, without a copy
                        outputBuilder.ProcessAnnexBTrackSample(targetVideoTrack.TrackID, new ArraySegment<byte>(naluBuffer, 0, (int)length));
                    }
                }

                Console.WriteLine(double.IsInfinity(seconds) ? "Press any key to exit" : $"Recording for {seconds} s");
                stopwatch.Start();
                long lastFrame = -1000;
                long frameDuration = 1000 * fpsDenom / fpsNom; // in milliseconds

                while (stopwatch.Elapsed.TotalSeconds < seconds && (Console.IsInputRedirected || !Console.KeyAvailable))
                {
                    if (stopwatch.ElapsedMilliseconds - lastFrame < frameDuration)
                    {
                        await Task.Delay(5);
                        continue;
                    }

                    lastFrame = stopwatch.ElapsedMilliseconds;

                    if (!camera.ReadSample(frameBuffer, out var timestamp))
                        continue;

                    byte[] nv12 = nv12Buffer;
                    if (!converts)
                    {
                        nv12 = frameBuffer;
                    }
                    else
                    {
                        if (!colorConverter.ProcessInput(frameBuffer, timestamp) || !colorConverter.ProcessOutput(ref nv12Buffer, out _))
                            continue;
                        nv12 = nv12Buffer;
                    }

                    if (videoEncoder.ProcessInput(nv12, timestamp))
                        Write();
                }

                // what the encoder still holds, before the file is finished
                videoEncoder.BeginDrain();
                Write();
                videoEncoder.EndDrain();
            }
        }

        outputBuilder.FinalizeMedia();
    }
}
