using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation.Input
{
    public class DeviceSource : IDisposable
    {
        public static readonly Guid IID_IMFMediaSource = new Guid("279A808D-AEC7-40C8-9C6B-A6B492C78A66");
        public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;

        private IMFSourceReader _pReader;
        private bool _disposedValue;
        
        public uint Width { get; private set; }
        public uint Height { get; private set; }

        public Guid OutputFormat { get; private set; }
        public uint OutputSize { get; private set; }

        static DeviceSource()
        {
            MFUtils.Check(PInvoke.MFStartup(PInvoke.MF_API_VERSION, 0));
        }

        public DeviceSource()
        { }

        public void Initialize()
        {
            CaptureDevice[] devices = FindVideoCaptureDevices();
            var device = (IMFMediaSource)devices[0].Activator.ActivateObject(IID_IMFMediaSource);
            ReleaseVideoCaptureDevices(devices);
            _pReader = CreateSourceReader(device);
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
            while(!ReadSample(_pReader, ref sample, out _, out _, out _, out sampleSize) && i++ < 2)
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

            pReader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &lactualStreamIndex, &ldwStreamFlags, &ltimestamp, out sample);

            actualStreamIndex = lactualStreamIndex;
            dwStreamFlags = ldwStreamFlags;
            timestamp = ltimestamp;

            if (sample != null)
            {
                sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
                try
                {
                    return MFUtils.CopyBuffer(buffer, sampleBytes, out sampleSize);
                }
                finally
                {
                    Marshal.ReleaseComObject(buffer);
                    Marshal.ReleaseComObject(sample);
                }
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
                    Debug.WriteLine(ex.Message);
                    break;
                }

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
            MFUtils.Check(PInvoke.MFCreateAttributes(out IMFAttributes pSrcConfig, 1));
            MFUtils.Check(PInvoke.MFCreateSourceReaderFromMediaSource(device, pSrcConfig, out IMFSourceReader pReader));
            return pReader;
        }

        private static unsafe void ReleaseVideoCaptureDevices(CaptureDevice[] devices)
        {
            for (int i = 0; i < devices.Length; i++)
            {
                Marshal.ReleaseComObject(devices[i].Activator);
            }
        }

        public class CaptureDevice
        {
            public IMFActivate Activator { get; set; }
            public string Name { get; set; }
            public CaptureDevice(IMFActivate activator, string name)
            {
                this.Activator = activator;
                this.Name = name;
            }
        }

        public static unsafe CaptureDevice[] FindVideoCaptureDevices()
        {
            List<CaptureDevice> ret = new List<CaptureDevice>();
            MFUtils.Check(PInvoke.MFCreateAttributes(out var pConfig, 1));
            pConfig.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            MFUtils.Check(PInvoke.MFEnumDeviceSources(pConfig, out var devices, out uint pcSourceActivate));
            for (int i = 0; i < devices.Length; i++)
            {
                devices[i].GetAllocatedString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, out PWSTR name, out _);
                Debug.WriteLine($"Found device {name}");
                ret.Add(new CaptureDevice(devices[i], name.ToString()));
            }
            return ret.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                { }

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
