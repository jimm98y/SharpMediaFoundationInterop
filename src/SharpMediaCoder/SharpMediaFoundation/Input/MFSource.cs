using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Input
{
    public class MFSource
    {
        public readonly Guid IMF_MEDIA_SOURCE = new Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66");

        public MFSource()
        {
            Initialize();
        }

        private unsafe void Initialize()
        {
            IMFActivate[] devices;
            IMFAttributes pConfig;
            MFTUtils.Check(PInvoke.MFCreateAttributes(out pConfig, 1));
            pConfig.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            MFTUtils.Check(PInvoke.MFEnumDeviceSources(pConfig, out devices, out uint pcSourceActivate));

            // https://learn.microsoft.com/en-us/windows/win32/medfound/audio-video-capture-in-media-foundation
            var device = (IMFMediaSource)devices[0].ActivateObject(IMF_MEDIA_SOURCE);
            for (int i = 0; i < devices.Length; i++)
            {
                devices[i].GetAllocatedString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, out PWSTR name, out _);
                Debug.WriteLine($"Found device {name}");
                Marshal.ReleaseComObject(devices[i]);
            }

            IMFAttributes pSrcConfig;
            MFTUtils.Check(PInvoke.MFCreateAttributes(out pSrcConfig, 1));
            pSrcConfig.SetUINT32(PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1); // this allows us to convert native YUY2 to NV12
            pSrcConfig.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            
            IMFSourceReader pReader;
            MFTUtils.Check(PInvoke.MFCreateSourceReaderFromMediaSource(device, pSrcConfig, out pReader));

            IMFMediaType mediaType;
            MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaType));
            mediaType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);

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

                        // TODO: YUY2 to NV12 converter
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
                    }

                    Marshal.ReleaseComObject(sample);
                }

                Thread.Sleep(100);
            }
        }
    }
}
