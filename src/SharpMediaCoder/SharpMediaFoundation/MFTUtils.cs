using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundation
{
    public static class MFTUtils
    {
        public static readonly Guid IID_IMFTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");

        public static IMFTransform CreateTransform(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
        {
            IMFTransform transform = default;
            foreach (IMFActivate activate in FindTransforms(category, flags, input, output))
            {
                try
                {
                    activate.GetAllocatedString(PInvoke.MFT_FRIENDLY_NAME_Attribute, out PWSTR name, out _);
                    Debug.WriteLine($"Found MFT: {name}");
                    transform = activate.ActivateObject(IID_IMFTransform) as IMFTransform;
                    break;
                }
                finally
                {
                    Marshal.ReleaseComObject(activate);
                }
            }

            return transform;
        }

        public static void DestroyTransform(IMFTransform transform)
        {
            Marshal.ReleaseComObject(transform);
        }

        public static IEnumerable<IMFActivate> FindTransforms(Guid category, MFT_ENUM_FLAG flags, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
        {
            Check(PInvoke.MFTEnumEx(category, flags, input, output, out IMFActivate[] activates, out uint activateCount));

            if (activateCount > 0)
            {
                foreach (IMFActivate activate in activates)
                {
                    yield return activate;
                }
            }
        }

        public static void Check(HRESULT result)
        {
            if (result.Failed)
                Marshal.ThrowExceptionForHR(result.Value);
        }

        public static unsafe IMFSample CreateSample(byte[] data, long sampleDuration, long timestamp)
        {
            Check(PInvoke.MFCreateMemoryBuffer((uint)data.Length, out IMFMediaBuffer buffer));

            try
            {
                uint maxLength = default;
                uint currentLength = default;
                byte* target = default;
                buffer.Lock(&target, &maxLength, &currentLength);
                fixed (byte* source = data)
                {
                    Unsafe.CopyBlock(target, source, (uint)data.Length);
                }
            }
            finally
            {
                buffer.SetCurrentLength((uint)data.Length);
                buffer.Unlock();
            }

            Check(PInvoke.MFCreateSample(out IMFSample sample));

            sample.AddBuffer(buffer);
            sample.SetSampleDuration(sampleDuration);
            sample.SetSampleTime(timestamp);
            return sample;
        }

        public static MFT_OUTPUT_DATA_BUFFER[] CreateOutputDataBuffer(uint size = 0)
        {
            MFT_OUTPUT_DATA_BUFFER[] result = new MFT_OUTPUT_DATA_BUFFER[1];

            if (size > 0)
            {
                Check(PInvoke.MFCreateMemoryBuffer(size, out IMFMediaBuffer buffer));
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

        public static long CalculateSampleDuration(uint fpsNom, uint fpsDenom)
        {
            ulong sampleDuration;
            Check(PInvoke.MFFrameRateToAverageTimePerFrame(fpsNom, fpsDenom, out sampleDuration));
            return (long)sampleDuration;
        }

        public static ulong EncodeAttributeValue(uint highValue, uint lowValue)
        {
            return ((ulong)highValue << 32) + lowValue;
        }
    }
}
