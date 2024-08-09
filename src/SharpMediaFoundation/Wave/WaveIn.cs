using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace SharpMediaFoundation.Wave
{
    public class WaveInDevice
    {
        public uint DeviceID { get; }
        public uint Formats { get; }
        public string Name { get; }
        public ushort Channels { get; }
        public uint DriverVersion { get; }
        public ushort Mid { get; }
        public ushort Pid { get; }

        public WaveInDevice(uint deviceID, uint formats, string name, ushort channels, uint driverVersion, ushort mid, ushort pid)
        {
            DeviceID = deviceID;
            Formats = formats;
            Name = name;
            Channels = channels;
            DriverVersion = driverVersion;
            Mid = mid;
            Pid = pid;
        }
    }

    public class WaveInEventArgs : EventArgs
    {
        public byte[] Data { get; private set; }

        public WaveInEventArgs(byte[] data)
        {
            this.Data = data;
        }
    }

    public class WaveIn : IDisposable
    {
        public const int MM_WIM_DATA = 0x3C0;
        public const uint MMSYSERR_NOERROR = 0;
        public const uint WAVE_MAPPER = unchecked((uint)-1);

        private HWAVEIN _hDevice;

        private const int _audioBufferSize = 1024 * 1024;
        private nint _audioBuffer = nint.Zero;
        private uint _audioBufferIndex = 0;

        public event EventHandler<WaveInEventArgs> FrameReceived;

        const int NUM_BUF = 3;

        private bool _disposedValue;

        // https://github.com/microsoft/CsWin32/issues/623
        private Delegate _callback; // hold on to the delegate so that it does not get garbage collected

        public void Initialize(uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Initialize(WAVE_MAPPER, samplesPerSecond, channels, bitsPerSample);
        }

        public unsafe void Initialize(uint deviceID, uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Close();

            if(_audioBuffer == nint.Zero)
            {
                _audioBuffer = Marshal.AllocHGlobal(_audioBufferSize);
            }

            WAVEFORMATEX waveFormat = new WAVEFORMATEX();
            waveFormat.nAvgBytesPerSec = samplesPerSecond * (bitsPerSample / 8) * channels;
            waveFormat.nBlockAlign = (ushort)(channels * (bitsPerSample / 8));
            waveFormat.nChannels = (ushort)channels;
            waveFormat.nSamplesPerSec = samplesPerSecond;
            waveFormat.wBitsPerSample = (ushort)bitsPerSample;
            waveFormat.cbSize = 0;
            waveFormat.wFormatTag = 1; // pcm

            HWAVEIN device;
            _callback = DoneCallback;
            PInvoke.waveInOpen(&device, deviceID, &waveFormat, (nuint)Marshal.GetFunctionPointerForDelegate(_callback), nuint.Zero, MIDI_WAVE_OPEN_TYPE.CALLBACK_FUNCTION);
            this._hDevice = device;

            uint audioBufferIndex = 0;
            for (int i = 0; i < NUM_BUF; i++)
            {
                WAVEHDR* waveHdr = (WAVEHDR*)((byte*)_audioBuffer + audioBufferIndex);
                waveHdr->lpData = (byte*)_audioBuffer + audioBufferIndex + (uint)sizeof(WAVEHDR);
                waveHdr->dwBufferLength = samplesPerSecond * channels * bitsPerSample / 8;
                waveHdr->dwUser = nuint.Zero;
                waveHdr->dwFlags = 0;
                waveHdr->dwLoops = 0;
                PInvoke.waveInPrepareHeader(this._hDevice, waveHdr, (uint)sizeof(WAVEHDR));
                audioBufferIndex = audioBufferIndex + (uint)sizeof(WAVEHDR) + waveHdr->dwBufferLength;
            }

            PInvoke.waveInAddBuffer(this._hDevice, (WAVEHDR*)(_audioBuffer + _audioBufferIndex), (uint)sizeof(WAVEHDR));
            PInvoke.waveInStart(this._hDevice);
        }

        private unsafe void DoneCallback(HWAVEIN* dev, uint uMsg, uint* dwUser, uint dwParam1, uint dwParam2)
        {
            if (uMsg == MM_WIM_DATA)
            {
                WAVEHDR* waveHdr = (WAVEHDR*)(_audioBuffer + _audioBufferIndex);
                byte[] dest = new byte[waveHdr->dwBufferLength];
                Marshal.Copy((nint)waveHdr->lpData.Value, dest, 0, dest.Length);
                _audioBufferIndex = (_audioBufferIndex + (uint)sizeof(WAVEHDR) + waveHdr->dwBufferLength) % (NUM_BUF * (waveHdr->dwBufferLength + (uint)sizeof(WAVEHDR)));

                waveHdr = (WAVEHDR*)(_audioBuffer + _audioBufferIndex);
                PInvoke.waveInAddBuffer(_hDevice, waveHdr, (uint)sizeof(WAVEHDR));

                FrameReceived?.Invoke(this, new WaveInEventArgs(dest));
            }
        }

        public void Reset()
        {
            PInvoke.waveInStop(_hDevice);
            _audioBufferIndex = 0;
        }

        public void Close()
        {
            Reset();
            PInvoke.waveInClose(_hDevice);
        }

        public static unsafe WaveInDevice[] Enumerate()
        {
            uint deviceCount = PInvoke.waveInGetNumDevs();
            List<WaveInDevice> ret = new List<WaveInDevice>();
            for (int i = 0; i < deviceCount; i++)
            {
                uint deviceID = (uint)i;
                WAVEINCAPS2W caps = new WAVEINCAPS2W();
                uint result = PInvoke.waveInGetDevCapsW(deviceID, (WAVEINCAPSW*)&caps, (uint)Marshal.SizeOf<WAVEINCAPS2W>());
                if (result == MMSYSERR_NOERROR)
                {
                    string name = null;
                    try
                    {
                        RegistryKey namesKey = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\MediaCategories");
                        if (namesKey != null)
                        {
                            RegistryKey nameKey = namesKey.OpenSubKey(caps.NameGuid.ToString("B"));
                            if (nameKey != null) name = nameKey.GetValue("Name") as string;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Log.ErrorEnabled) Log.Error(ex.Message);
                    }

                    ret.Add(
                        new WaveInDevice(
                            deviceID,
                            caps.dwFormats,
                            name ?? caps.szPname.ToString(),
                            caps.wChannels,
                            caps.vDriverVersion,
                            caps.wMid,
                            caps.wPid
                        ));
                }
                else
                {
                    throw new Exception($"Wave device enumeration failed with error {result}.");
                }
            }
            return ret.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    Close();
                }

                if (_audioBuffer != nint.Zero)
                {
                    Marshal.FreeHGlobal(_audioBuffer);
                    _audioBuffer = nint.Zero;
                }

                _disposedValue = true;
            }
        }

        ~WaveIn()
        {
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
