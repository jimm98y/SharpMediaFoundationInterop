using System.Diagnostics;
using SharpMediaFoundationInterop.Input;
using SharpMediaFoundationInterop.Transforms.H265;
using SharpMediaFoundationInterop.Transforms.Colors;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Tracks;
using SharpMP4.Builders;

const string targetFileName = "screen.mp4";
const uint fpsNom = 12000;
const uint fpsDenom = 1001;
Stopwatch stopwatch = new Stopwatch();

using (Stream output = new BufferedStream(new FileStream(targetFileName, FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    IMp4Builder outputBuilder = new Mp4Builder(new SingleStreamOutput(output));

    var targetVideoTrack = new H265Track();
    outputBuilder.AddTrack(targetVideoTrack);

    using (var screenCapture = new ScreenCapture())
    {
        screenCapture.Initialize();

        using (var videoEncoder = new H265Encoder(screenCapture.Width, screenCapture.Height, fpsNom, fpsDenom, 80000000))
        {
            videoEncoder.Initialize();

            using (var colorConverter = new ColorConverter(screenCapture.OutputFormat, videoEncoder.InputFormat, screenCapture.Width, screenCapture.Height))
            {
                colorConverter.Initialize();

                var rgbaBuffer = new byte[screenCapture.OutputSize];
                var nv12Buffer = new byte[colorConverter.OutputSize];
                var naluBuffer = new byte[videoEncoder.OutputSize];

                Console.WriteLine("Press any key to exit");
                stopwatch.Start();
                long lastframe = 0;
                long frameDuration = 1000 * fpsDenom / fpsNom;

                while (!Console.KeyAvailable)
                {
                    if (stopwatch.ElapsedTicks - lastframe < frameDuration)
                    {
                        await Task.Delay(10);
                        continue;
                    }

                    lastframe = stopwatch.ElapsedTicks;

                    if (screenCapture.ReadSample(rgbaBuffer, out var timestamp))
                    {
                        if (colorConverter.ProcessInput(rgbaBuffer, timestamp))
                        {
                            if (colorConverter.ProcessOutput(ref nv12Buffer, out _))
                            {
                                if (videoEncoder.ProcessInput(nv12Buffer, timestamp))
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
            }
        }

        outputBuilder.FinalizeMedia();
    }
}
