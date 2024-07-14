using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Input
{
    public class MFDeviceSource
    {
        public readonly Guid IMF_MEDIA_SOURCE = new Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66");

        public MFDeviceSource()
        {
            Initialize();
        }

        private unsafe void Initialize()
        {
            IMFActivate[] devices;
            IMFAttributes pConfig;
            MFTUtils.Check(PInvoke.MFCreateAttributes(out pConfig, 1));
            pConfig.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            pConfig.SetUINT32(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 0);
            MFTUtils.Check(PInvoke.MFEnumDeviceSources(pConfig, out devices, out uint pcSourceActivate));

            // https://learn.microsoft.com/en-us/windows/win32/medfound/audio-video-capture-in-media-foundation
            for (int i = 0; i < devices.Length; i++)
            {
                devices[i].GetAllocatedString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, out PWSTR name, out _);
                Debug.WriteLine($"Found device {name}");
            }

            var device = (IMFMediaSource)devices[0].ActivateObject(IMF_MEDIA_SOURCE);

            for (int i = 0; i < devices.Length; i++)
            {
                Marshal.ReleaseComObject(devices[i]);
            }

            IMFAttributes pSrcConfig;
            MFTUtils.Check(PInvoke.MFCreateAttributes(out pSrcConfig, 1));
            
            // this allows us to convert native YUY2 (YUYV) to NV12
            pSrcConfig.SetUINT32(PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            pSrcConfig.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            
            IMFSourceReader pReader;
            MFTUtils.Check(PInvoke.MFCreateSourceReaderFromMediaSource(device, pSrcConfig, out pReader));

            IMFMediaType nativeMediaType;
            Guid majorType = Guid.Empty;
            Guid subtype = Guid.Empty;
            ulong frameSize = 0;
            uint dwMediaTypeIndex = 0;
            while (true)
            {
                try
                {
                    pReader.GetNativeMediaType(0, dwMediaTypeIndex++, out nativeMediaType);
                }
                catch(COMException ex)
                {
                    Debug.WriteLine(ex.Message);
                    break;
                }

                nativeMediaType.GetGUID(PInvoke.MF_MT_MAJOR_TYPE, out majorType);
                nativeMediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out subtype);

                ulong fs = 0;
                nativeMediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out fs);
                if (fs > frameSize)
                    frameSize = fs;

                int width = (int)(frameSize >> 32);
                int height = (int)(frameSize & 0xFFFFFFFF);
            }

            IMFMediaType mediaType;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaType));
            mediaType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            mediaType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, frameSize);
            pReader.SetCurrentMediaType(0, mediaType);

            uint actualStreamIndex;
            uint dwStreamFlags;
            long timestamp;
            IMFSample sample;
            byte[] sampleBytes = null;

            while (true)
            {
                pReader.ReadSample(0, 0, &actualStreamIndex, &dwStreamFlags, &timestamp, out sample);

                if (sample != null)
                {
                    sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                    try
                    {
                        uint maxLength = default;
                        uint currentLength = default;
                        byte* data = default;
                        buffer.Lock(&data, &maxLength, &currentLength);

                        if (sampleBytes == null)
                        {
                            sampleBytes = new byte[maxLength];
                        }

                        Marshal.Copy((IntPtr)data, sampleBytes, 0, (int)currentLength);
                    }
                    finally
                    {
                        buffer.SetCurrentLength(0);
                        buffer.Unlock();
                        Marshal.ReleaseComObject(buffer);
                    }

                    Marshal.ReleaseComObject(sample);
                }

                Thread.Sleep(100);
            }
        }
    }
}
