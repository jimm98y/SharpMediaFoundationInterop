using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpMediaFoundationInterop.Transforms;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace SharpMediaFoundationInterop.Input
{
    public class CaptureDevice
    {
        public string ID { get; private set; }
        public string Name { get; private set; }

        public CaptureDevice(string name, string id)
        {
            this.Name = name;
            this.ID = id;
        }
    }

    public class DeviceCapture : IMediaVideoSource
    {
        public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;

        private IMFSourceReader _pReader;
        private bool _disposedValue;
        
        public uint Width { get; private set; }
        public uint Height { get; private set; }
        public uint OriginalWidth { get { return Width; } }
        public uint OriginalHeight { get { return Height; } }

        public Guid OutputFormat { get; private set; }
        public uint OutputSize { get; private set; }

        static DeviceCapture()
        {
            MediaUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        public void Initialize()
        {
            Initialize((string)null);
        }

        public void Initialize(CaptureDevice device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            Initialize(device.ID);
        }

        public void Initialize(string symbolicLink)
        {
            IMFMediaSource device = GetCaptureDevice(symbolicLink);
            _pReader = CreateSourceReader(device);

            // TODO: make configurable
            var mediaType = GetBestMediaType(_pReader);

            mediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var frameSize);
            Width = (uint)(frameSize >> 32);
            Height = (uint)(frameSize & 0xFFFFFFFF);
            mediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var targetFormat);
            OutputFormat = targetFormat;
            _pReader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, mediaType);

            uint sampleSize;
            byte[] sample = null;
            int i = 0;
            // right now I know of no better solution to get the sample size than to read a sample
            while (!ReadSample(_pReader, ref sample, out _, out _, out _, out sampleSize) && i++ < 2)
            { }
            OutputSize = sampleSize;
        }

        public bool ReadSample(byte[] sampleBytes, out long timestamp)
        {
            return ReadSample(_pReader, ref sampleBytes, out _, out _, out timestamp, out _);
        }

        private static unsafe bool ReadSample(IMFSourceReader pReader, ref byte[] sampleBytes, out uint actualStreamIndex, out uint dwStreamFlags, out long timestamp, out uint sampleSize)
        {
            IMFSample sample;
            uint lactualStreamIndex;
            uint ldwStreamFlags;
            long ltimestamp;

            pReader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, out lactualStreamIndex, out ldwStreamFlags, out ltimestamp, out sample);

            actualStreamIndex = lactualStreamIndex;
            dwStreamFlags = ldwStreamFlags;
            timestamp = ltimestamp;

            if (sample != null)
            {
                sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                bool ret =  MediaUtils.CopyBuffer(buffer, sampleBytes, out sampleSize);
                GC.AddMemoryPressure(sampleSize); // samples are large, so to keep the memory usage low we have to tell GC about large amounts of unmanaged memory being allocated

                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
                GC.RemoveMemoryPressure(sampleSize);
                return ret;
            }

            sampleSize = 0;
            return false;
        }

        private static unsafe IMFMediaType GetBestMediaType(IMFSourceReader pReader)
        {
            IMFMediaType nativeMediaType;
            IMFMediaType bestMediaType = null;
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
                    if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
                    break;
                }

                // check for the target format and if it's MJPG, skip it
                nativeMediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var targetFormat);
                if (
                    targetFormat != PInvoke.MFVideoFormat_RGB24 &&
                    targetFormat != PInvoke.MFVideoFormat_RGB32 &&
                    targetFormat != PInvoke.MFVideoFormat_RGB555 &&
                    targetFormat != PInvoke.MFVideoFormat_RGB565 &&
                    targetFormat != PInvoke.MFVideoFormat_RGB8 &&
                    targetFormat != PInvoke.MFVideoFormat_A2R10G10B10 &&
                    targetFormat != PInvoke.MFVideoFormat_A16B16G16R16F &&

                    targetFormat != PInvoke.MFVideoFormat_AI44 &&
                    targetFormat != PInvoke.MFVideoFormat_AYUV &&
                    targetFormat != PInvoke.MFVideoFormat_I420 &&
                    targetFormat != PInvoke.MFVideoFormat_IYUV &&
                    targetFormat != PInvoke.MFVideoFormat_NV11 &&
                    targetFormat != PInvoke.MFVideoFormat_NV12 &&
                    targetFormat != PInvoke.MFVideoFormat_NV21 &&
                    targetFormat != PInvoke.MFVideoFormat_UYVY &&
                    targetFormat != PInvoke.MFVideoFormat_Y41P &&
                    targetFormat != PInvoke.MFVideoFormat_Y41T &&
                    targetFormat != PInvoke.MFVideoFormat_Y42T &&
                    targetFormat != PInvoke.MFVideoFormat_YUY2 &&
                    targetFormat != PInvoke.MFVideoFormat_YVU9 &&
                    targetFormat != PInvoke.MFVideoFormat_YV12 &&
                    targetFormat != PInvoke.MFVideoFormat_YVYU &&

                    //targetFormat != PInvoke.MFVideoFormat_I422 &&
                    //targetFormat != PInvoke.MFVideoFormat_I444 &&
                    targetFormat != PInvoke.MFVideoFormat_P010 &&
                    targetFormat != PInvoke.MFVideoFormat_P016 &&
                    targetFormat != PInvoke.MFVideoFormat_P210 &&
                    targetFormat != PInvoke.MFVideoFormat_P216 &&
                    targetFormat != PInvoke.MFVideoFormat_v210 &&
                    targetFormat != PInvoke.MFVideoFormat_v216 &&
                    targetFormat != PInvoke.MFVideoFormat_v410 &&
                    targetFormat != PInvoke.MFVideoFormat_Y210 &&
                    targetFormat != PInvoke.MFVideoFormat_Y216 &&
                    targetFormat != PInvoke.MFVideoFormat_Y410 &&
                    targetFormat != PInvoke.MFVideoFormat_Y416 &&

                    targetFormat != PInvoke.MFVideoFormat_L8 &&
                    targetFormat != PInvoke.MFVideoFormat_L16 &&
                    targetFormat != PInvoke.MFVideoFormat_D16                     
                    )
                    continue;

                nativeMediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out ulong currentFrameSize);
                if (currentFrameSize > frameSize)
                {
                    frameSize = currentFrameSize;
                    bestMediaType = nativeMediaType;
                }
            }

            return bestMediaType;
        }

        private static unsafe IMFSourceReader CreateSourceReader(IMFMediaSource device)
        {
            MediaUtils.Check(PInvoke.MFCreateAttributes(out IMFAttributes pSrcConfig, 1));
            MediaUtils.Check(PInvoke.MFCreateSourceReaderFromMediaSource(device, pSrcConfig, out IMFSourceReader pReader));
            return pReader;
        }

        private static unsafe IMFMediaSource GetCaptureDevice(string symbolicLink)
        {
            MediaUtils.Check(PInvoke.MFCreateAttributes(out IMFAttributes pConfig, 1));
            pConfig.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            MediaUtils.Check(PInvoke.MFEnumDeviceSources(pConfig, out IMFActivate_unmanaged** devices, out uint devicesCount));
            int deviceIndex;
            for (deviceIndex = 0; deviceIndex < devicesCount; deviceIndex++)
            {
                devices[deviceIndex]->GetAllocatedString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, out PWSTR sLink, out _);
                if (string.IsNullOrEmpty(symbolicLink))
                {
                    // we take the first device available
                    break;
                }
                else if (symbolicLink == sLink.ToString())
                {
                    break;
                }
                else if (deviceIndex == devicesCount - 1)
                {
                    throw new Exception($"Device {symbolicLink} was not found!");
                }
            }
            IUnknown* device = (IUnknown*)devices[deviceIndex]->ActivateObject(typeof(IMFMediaSource).GUID);
            for (deviceIndex = 0; deviceIndex < devicesCount; deviceIndex++)
            {
                devices[deviceIndex]->Release();
            }
            Marshal.ReleaseComObject(pConfig);

            var ret = (IMFMediaSource)Marshal.GetObjectForIUnknown((nint)device);
            device->Release();
            device = null;
            return ret;
        }

        public static unsafe CaptureDevice[] Enumerate()
        {
            List<CaptureDevice> ret = new List<CaptureDevice>();
            MediaUtils.Check(PInvoke.MFCreateAttributes(out IMFAttributes pConfig, 1));
            pConfig.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            MediaUtils.Check(PInvoke.MFEnumDeviceSources(pConfig, out IMFActivate_unmanaged** devices, out uint devicesCount));
            for (int i = 0; i < devicesCount; i++)
            {
                devices[i]->GetAllocatedString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, out PWSTR name, out _);
                devices[i]->GetAllocatedString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, out PWSTR symbolicLink, out _);
                ret.Add(new CaptureDevice(name.ToString(), symbolicLink.ToString()));
                devices[i]->Release();
            }
            Marshal.ReleaseComObject(pConfig);
            return ret.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (_pReader != null)
                {
                    Marshal.ReleaseComObject(_pReader);
                    _pReader = null;
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
