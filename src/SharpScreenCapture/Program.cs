using System.Diagnostics;
using System.Globalization;
using SharpMediaFoundationInterop;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Tracks;
using SharpMP4.Builders;

const string targetFileName = "screen.mp4";
const uint fpsNom = 12000;
const uint fpsDenom = 1001;
// how long to record, in seconds, as the first argument; or, of none, until a key is pressed
double seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : double.PositiveInfinity;
Stopwatch stopwatch = new Stopwatch();

using (Stream output = new BufferedStream(new FileStream(targetFileName, FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    IMp4Builder outputBuilder = new Mp4Builder(new SingleStreamOutput(output));

    var targetVideoTrack = new H265Track();
    outputBuilder.AddTrack(targetVideoTrack);

    using (var screenCapture = MediaDevices.CreateScreenCapture())
    {
        screenCapture.Initialize();

        var encoderOptions = new VideoEncoderOptions
        {
            Width = screenCapture.Width,
            Height = screenCapture.Height,
            FpsNom = fpsNom,
            FpsDenom = fpsDenom,
            Bitrate = 80000000,
        };
        using (var videoEncoder = MediaCodecs.CreateVideoEncoder(VideoCodec.H265, encoderOptions))
        {
            videoEncoder.Initialize();

            // to NV12, of what the screen capture hands out: as it is where it is NV12 already, as a Mac's camera's frames are; of
            // Media Foundation's color converter on Windows; and of BitmapUtils elsewhere, which converts BGRA
            bool converts = screenCapture.OutputFormat != videoEncoder.InputFormat;
            IMediaVideoTransform colorConverter = converts && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
                ? new ColorConverter(screenCapture.OutputFormat, videoEncoder.InputFormat, screenCapture.Width, screenCapture.Height)
                : null;
            if (converts && colorConverter == null && screenCapture.OutputFormat != MediaFormats.ARGB32)
                throw new NotSupportedException($"No conversion of {screenCapture.OutputFormat} to NV12 here");

            using (colorConverter)
            {
                colorConverter?.Initialize();

                var frameBuffer = new byte[screenCapture.OutputSize];
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

                    if (!screenCapture.ReadSample(frameBuffer, out var timestamp))
                        continue;

                    byte[] nv12 = nv12Buffer;
                    if (!converts)
                    {
                        nv12 = frameBuffer;
                    }
                    else if (colorConverter != null)
                    {
                        if (!colorConverter.ProcessInput(frameBuffer, timestamp) || !colorConverter.ProcessOutput(ref nv12Buffer, out _))
                            continue;
                        nv12 = nv12Buffer;
                    }
                    else
                    {
                        // BGRA, bottom-up, as the screen capture hands it out by default
                        BitmapUtils.ConvertBgraToNV12(frameBuffer, (int)screenCapture.Width, (int)screenCapture.Height, bottomUp: true, nv12Buffer);
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
