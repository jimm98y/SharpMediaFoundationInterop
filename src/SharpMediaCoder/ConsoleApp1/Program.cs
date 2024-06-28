using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

const Int32 DefaultVideoWidth = 640;
const Int32 DefaultVideoHeight = 480;
const Int64 DefaultFrameSize = ((Int64)DefaultVideoWidth << 32) + DefaultVideoHeight;

const Int32 DefaultInterlaceMode = 7;
const Int32 DefaultVideoFramesPerSecond = 30;
const Int64 DefaultPixelAspectRatio = (1L << 32) + 1;

Boolean isFirstFrame = true;

Check(PInvoke.MFFrameRateToAverageTimePerFrame(DefaultVideoFramesPerSecond, 1, out UInt64 sampleDuration));

Startup();

IMFTransform? decoder = CreateVideoDecoder();

if (decoder is null)
{
    Console.WriteLine("Unable to create a video decoder");
    return;
}

Byte[] file = File.ReadAllBytes(@"..\..\..\..\Video.h264");

MFT_OUTPUT_DATA_BUFFER[] videoData = CreateOutputDataBuffer(1024 * 1024);

IMFSample sampleToProcess = CreateSample(file, DateTime.Now.Ticks);

try
{
    ProcessVideoSample(sampleToProcess);
}
catch (Exception ex)
{
    Console.WriteLine($"Error while processing video sample {ex}");
}

Console.WriteLine("Press any key to TERMINATE the program...");
Console.ReadKey();

static void Check(HRESULT result)
{
    if (result.Failed) Marshal.ThrowExceptionForHR(result.Value);
}

static void Startup()
{
    Check(PInvoke.MFStartup(0x20070, 0));
}

static IMFMediaType CreateMediaType()
{
    Check(PInvoke.MFCreateMediaType(out IMFMediaType mediaType));

    return mediaType;
}

static IEnumerable<IMFActivate> FindTransforms(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
{
    Check(PInvoke.MFTEnumEx(category, flags, input, output, out IMFActivate[] activates, out UInt32 activateCount));

    if (activateCount > 0)
    {
        foreach (IMFActivate activate in activates)
        {
            yield return activate;
        }
    }
}

IMFTransform? CreateVideoDecoder()
{
    IMFTransform? result = default;

    foreach (IMFActivate activate in FindTransforms(PInvoke.MFT_CATEGORY_VIDEO_DECODER,
        MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
        new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 },
        new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_YUY2 }))
    {
        try
        {
            activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR deviceName, out _);
            Console.WriteLine($"Found video decoder MFT: {deviceName}");
            result = activate.ActivateObject(new Guid("BF94C121-5B05-4E6F-8000-BA598961414D")) as IMFTransform;
            break;
        }
        finally
        {
            Marshal.ReleaseComObject(activate);
        }
    }

    if (result is not null)
    {
        try
        {
            IMFMediaType mediaInput = CreateMediaType();

            mediaInput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaInput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);

            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
            mediaInput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, DefaultVideoFramesPerSecond);
            mediaInput.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, DefaultInterlaceMode);
            mediaInput.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, DefaultPixelAspectRatio);

            mediaInput.SetUINT32(PInvoke.MF_MT_COMPRESSED, 1);

            result.GetAttributes(out IMFAttributes attributes);

            attributes.SetUINT32(PInvoke.MF_LOW_LATENCY, 1);

            result.SetInputType(0, mediaInput, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while creating video decoder input media {ex}");
        }

        try
        {
            IMFMediaType mediaOutput = CreateMediaType();

            mediaOutput.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaOutput.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_YUY2);

            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, DefaultFrameSize);
            mediaOutput.SetUINT64(PInvoke.MF_MT_FRAME_RATE, DefaultVideoFramesPerSecond);

            result.SetOutputType(0, mediaOutput, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while creating video decoder output media {ex}");
        }
    }

    return result;
}

unsafe IMFSample CreateSample(Byte[] data, Int64 timestamp)
{
    Check(PInvoke.MFCreateMemoryBuffer((UInt32)data.Length, out IMFMediaBuffer buffer));

    try
    {
        UInt32* maxLength = default;
        UInt32* currentLength = default;

        buffer.Lock(out Byte* target, maxLength, currentLength);

        fixed (Byte* source = data)
        {
            Unsafe.CopyBlock(target, source, (UInt32)data.Length);
        }
    }
    finally
    {
        buffer.SetCurrentLength((UInt32)data.Length);
        buffer.Unlock();
    }

    Check(PInvoke.MFCreateSample(out IMFSample sample));

    sample.AddBuffer(buffer);
    sample.SetSampleDuration((Int64)sampleDuration);
    sample.SetSampleTime(timestamp);

    if (isFirstFrame)
    {
        isFirstFrame = false;

        sample.SetUINT32(PInvoke.MFSampleExtension_CleanPoint, 1);
        sample.SetUINT32(PInvoke.MFSampleExtension_Discontinuity, 1);
    }

    return sample;
}

static MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(Int32 size = 0)
{
    MFT_OUTPUT_DATA_BUFFER[] result = new MFT_OUTPUT_DATA_BUFFER[1];

    if (size > 0)
    {
        Check(PInvoke.MFCreateMemoryBuffer((UInt32)size, out IMFMediaBuffer buffer));
        Check(PInvoke.MFCreateSample(out IMFSample sample));

        sample.AddBuffer(buffer);

        result[0].pSample = sample;
    }
    else
    {
        result[0].pSample = default;
    }

    result[0].dwStreamID = 0;
    result[0].dwStatus = 0;
    result[0].pEvents = default;

    return result;
}

unsafe void ProcessVideoSample(IMFSample sample)
{
    try
    {
        Boolean isProcessed = false;

        HRESULT inputStatusResult = decoder.GetInputStatus(0, out uint decoderInputFlags);

        if (inputStatusResult.Value == 0 && decoderInputFlags == (uint)MftInputStatusFlags.AcceptData)
        {
            HRESULT inputResult = decoder.ProcessInput(0, sample, 0);

            if (inputResult.Value == 0)
            {
                do
                {
                    HRESULT outputResult = decoder.ProcessOutput(0, videoData, out uint decoderOutputStatus);

                    if (videoData[0].dwStatus == (uint)MFT_OUTPUT_DATA_BUFFERFlags.FormatChange)
                    {
                        decoder.GetOutputAvailableType(0, 0, out IMFMediaType? mediaType);

                        decoder.SetOutputType(0, mediaType, 0);
                        decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);

                        videoData[0].dwStatus = (uint)MFT_OUTPUT_DATA_BUFFERFlags.None;
                    }
                    else if (outputResult.Value == unchecked((int)0xc00d6d72))
                    {
                        // needs more input
                    }
                    else if (outputResult.Value == 0 && decoderOutputStatus == 0)
                    {
                        isProcessed = true;
                    }
                }
                while (videoData[0].dwStatus == (uint)MFT_OUTPUT_DATA_BUFFERFlags.Incomplete);
            }
        }

        if (isProcessed)
        {
            videoData[0].pSample.GetBufferByIndex(0, out IMFMediaBuffer buffer);
            try
            {
                UInt32* maxLength = default;
                UInt32* currentLength = default;

                buffer.Lock(out Byte* data, maxLength, currentLength);
            }
            finally
            {
                buffer.SetCurrentLength(0);
                buffer.Unlock();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error while processing input {ex}");
    }
    finally
    {
        Marshal.ReleaseComObject(sample);
    }
}

[Flags] public enum MftInputStatusFlags { AcceptData = 1 }
[Flags]
public enum MFT_OUTPUT_DATA_BUFFERFlags : uint
{
    None = 0x00,
    FormatChange = 0x100,
    Incomplete = 0x1000000,
}
