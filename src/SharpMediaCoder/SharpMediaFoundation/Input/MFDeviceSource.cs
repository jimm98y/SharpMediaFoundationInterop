﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Input
{
    public class MFDeviceSource
    {
        private const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
        public readonly Guid IMF_MEDIA_SOURCE = new Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66");
        private IMFSourceReader _pReader;

        private Guid _targetFormat = PInvoke.MFVideoFormat_RGB24; // PInvoke.MFVideoFormat_NV12;

        public MFDeviceSource()
        {
            IMFActivate[] devices = FindVideoCaptureDevices();
            var device = CreateVideoCaptureDevice(devices[0]);
            ReleaseVideoCaptureDevices(devices);
            _pReader = CreateSourceReader(device, _targetFormat);
            var bestMediaType = GetBestMediaType(_pReader);
            IMFMediaType mediaType = SetOutputMediaFormat(bestMediaType, _targetFormat);
            SetOutputMediaType(_pReader, mediaType);
        }

        public bool ReadSample(byte[] sampleBytes)
        {
            uint actualStreamIndex;
            uint dwStreamFlags;
            long timestamp;
            return ReadSample(_pReader, ref sampleBytes, out actualStreamIndex, out dwStreamFlags, out timestamp);
        }

        private static unsafe bool ReadSample(IMFSourceReader pReader, ref byte[] sampleBytes, out uint actualStreamIndex, out uint dwStreamFlags, out long timestamp)
        {
            IMFSample sample;
            uint lactualStreamIndex;
            uint ldwStreamFlags;
            long ltimestamp;

            pReader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &lactualStreamIndex, &ldwStreamFlags, &ltimestamp, out sample);

            actualStreamIndex = lactualStreamIndex;
            dwStreamFlags = ldwStreamFlags;
            timestamp = ltimestamp;

            if (sample != null)
            {
                sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                try
                {
                    uint maxLength = default;
                    uint currentLength = default;
                    byte* data = default;
                    buffer.Lock(&data, &maxLength, &currentLength);

                    Marshal.Copy((IntPtr)data, sampleBytes, 0, (int)currentLength);

                    return true;
                }
                finally
                {
                    buffer.SetCurrentLength(0);
                    buffer.Unlock();
                    Marshal.ReleaseComObject(buffer);
                    Marshal.ReleaseComObject(sample);
                }
            }

            return false;
        }

        private static unsafe void SetOutputMediaType(IMFSourceReader pReader, IMFMediaType mediaType)
        {
            pReader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, mediaType);
        }

        private static unsafe IMFMediaType SetOutputMediaFormat(IMFMediaType mediaType, Guid targetFormat)
        {
            //IMFMediaType mediaType;
            //MFTUtils.Check(PInvoke.MFCreateMediaType(out mediaType));
            //mediaType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            mediaType.SetGUID(PInvoke.MF_MT_SUBTYPE, targetFormat); 
            //mediaType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, frameSize);
            //mediaType.SetUINT32(PInvoke.MF_MT_PAN_SCAN_APERTURE, 0);
            return mediaType;
        }

        private static unsafe IMFMediaType GetBestMediaType(IMFSourceReader pReader)
        {
            // TODO: convert this method to "get media info"
            IMFMediaType nativeMediaType = null;
            IMFMediaType bestMediaType = null;
            Guid majorType = Guid.Empty;
            Guid subtype = Guid.Empty;
            ulong frameSize = 0;
            uint dwMediaTypeIndex = 0;

            while (true)
            {
                try
                {
                    pReader.GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, dwMediaTypeIndex++, out nativeMediaType);
                }
                catch (COMException ex)
                {
                    Debug.WriteLine(ex.Message);
                    break;
                }

                nativeMediaType.GetGUID(PInvoke.MF_MT_MAJOR_TYPE, out majorType);
                nativeMediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out subtype);

                ulong fs = 0;
                nativeMediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out fs);
                if (fs > frameSize)
                {
                    frameSize = fs;
                    bestMediaType = nativeMediaType;
                }

                int width = (int)(frameSize >> 32);
                int height = (int)(frameSize & 0xFFFFFFFF);
            }

            return bestMediaType;
        }

        private static unsafe IMFSourceReader CreateSourceReader(IMFMediaSource device, Guid targetFormat)
        {
            IMFAttributes pSrcConfig;
            MFTUtils.Check(PInvoke.MFCreateAttributes(out pSrcConfig, 1));

            // this allows us to convert native YUY2 (YUYV) to NV12
            pSrcConfig.SetUINT32(PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);
            pSrcConfig.SetUINT32(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            pSrcConfig.SetGUID(PInvoke.MF_MT_SUBTYPE, targetFormat);
            
            IMFSourceReader pReader;
            MFTUtils.Check(PInvoke.MFCreateSourceReaderFromMediaSource(device, pSrcConfig, out pReader));
            return pReader;
        }

        private unsafe IMFMediaSource CreateVideoCaptureDevice(IMFActivate device)
        {
            return (IMFMediaSource)device.ActivateObject(IMF_MEDIA_SOURCE);
        }

        private static unsafe void ReleaseVideoCaptureDevices(IMFActivate[] devices)
        {
            for (int i = 0; i < devices.Length; i++)
            {
                Marshal.ReleaseComObject(devices[i]);
            }
        }

        private static unsafe IMFActivate[] FindVideoCaptureDevices()
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

            return devices;
        }
    }
}
