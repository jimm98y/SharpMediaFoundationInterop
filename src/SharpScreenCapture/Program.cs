using System.Diagnostics;
using SharpMp4;
using SharpMediaFoundation.Input;
using SharpMediaFoundation.Transforms.H265;
using SharpMediaFoundation.Transforms.Colors;
using SharpMediaFoundation.Utils;

const string targetFileName = "screen.mp4";
const uint fpsNom = 12000;
const uint fpsDenom = 1001;
Stopwatch stopwatch = new Stopwatch();

using (Stream targetFileStream = new BufferedStream(new FileStream(targetFileName, FileMode.Create, FileAccess.Write, FileShare.Read)))
{
    using (FragmentedMp4Builder targetFile = new FragmentedMp4Builder(new SingleStreamOutput(targetFileStream)))
    {
        var targetVideoTrack = new H265Track();
        targetFile.AddTrack(targetVideoTrack);

        using (var screenCapture = new ScreenCapture())
        {
            screenCapture.Initialize();

            using (var videoEncoder = new H265Encoder(screenCapture.Width, screenCapture.Height, fpsNom, fpsDenom))
            {
                videoEncoder.Initialize();

                ColorConverter colorConverter = new ColorConverter(screenCapture.OutputFormat, videoEncoder.InputFormat, screenCapture.Width, screenCapture.Height);
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

                    if(screenCapture.ReadSample(rgbaBuffer, out var timestamp))
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
                                            await targetVideoTrack.ProcessSampleAsync(targetNALU);
                                        }
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
