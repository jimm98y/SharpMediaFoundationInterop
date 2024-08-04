using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Media;
using Windows.Win32.Media.Audio;

namespace SharpMediaFoundation.Wave
{
    public class WaveOutDevice
    {
        public uint DeviceID { get; }
        public uint Formats { get; }
        public string Name { get; }
        public ushort Channels { get; }
        public uint DriverVersion { get; }
        public ushort Mid { get; }
        public ushort Pid { get; }

        public WaveOutDevice(uint deviceID, uint formats, string name, ushort channels, uint driverVersion, ushort mid, ushort pid)
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
    
    public class WaveOut : IDisposable
    {
        const int TIME_MS = 0x0001;
        const int TIME_SAMPLES = 0x0002;
        const int TIME_BYTES = 0x0004;
        const int TIME_SMPTE = 0x0008;
        const int TIME_MIDI = 0x0010;
        const int TIME_TICKS = 0x0020;

        public const int MM_WOM_DONE = 0x3BD;
        public const uint MMSYSERR_NOERROR = 0;
        public const uint WAVE_MAPPER = unchecked((uint)-1);

        private HWAVEOUT _hDevice;

        private int _queuedFrames = 0;
        public int QueuedFrames {  get { return _queuedFrames; } }

        private const int _audioBufferSize = 1024 * 1024; 
        private nint _audioBuffer = nint.Zero;   
        private uint _audioBufferIndex = 0;

        private bool _disposedValue;

        public event EventHandler<EventArgs> OnPlaybackCompleted;

        // https://github.com/microsoft/CsWin32/issues/623
        private Delegate _callback; // hold on to the delegate so that it does not get garbage collected

        public void Initialize(uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Initialize(WAVE_MAPPER, samplesPerSecond, channels, bitsPerSample);
        }

        public unsafe void Initialize(uint deviceID, uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Close();

            if (_audioBuffer == nint.Zero)
            { 
                _audioBuffer = Marshal.AllocHGlobal(_audioBufferSize);
            }

            _callback = DoneCallback;
            WAVEFORMATEX waveFormat = new WAVEFORMATEX();  
            waveFormat.nAvgBytesPerSec = samplesPerSecond * (bitsPerSample / 8) * channels;
            waveFormat.nBlockAlign = (ushort)(channels * (bitsPerSample / 8));
            waveFormat.nChannels = (ushort)channels;
            waveFormat.nSamplesPerSec = samplesPerSecond;
            waveFormat.wBitsPerSample = (ushort)bitsPerSample;
            waveFormat.cbSize = 0;
            waveFormat.wFormatTag = 1; // pcm

            HWAVEOUT device;
            PInvoke.waveOutOpen(&device, deviceID, &waveFormat, (nuint)Marshal.GetFunctionPointerForDelegate(_callback), nuint.Zero, MIDI_WAVE_OPEN_TYPE.CALLBACK_FUNCTION); 
            this._hDevice = device;
            Reset();
        }

        private unsafe void DoneCallback(HWAVEOUT* dev, uint uMsg, uint* dwUser, uint dwParam1, uint dwParam2)
        {
            if (uMsg == MM_WOM_DONE)
            {
                Interlocked.Decrement(ref _queuedFrames);
                OnPlaybackCompleted?.Invoke(this, new EventArgs());
            }
        }

        public unsafe uint GetPosition()
        {
            MMTIME time = new MMTIME();
            time.wType = TIME_SAMPLES;
            PInvoke.waveOutGetPosition(_hDevice, &time, (uint)sizeof(MMTIME));
            if (time.wType != TIME_SAMPLES)
                throw new Exception("WaveOut device does not support reading time in samples");
            return time.u.sample;
        }

        public void Reset()
        {
            PInvoke.waveOutRestart(_hDevice);
            Interlocked.Exchange(ref _queuedFrames, 0);
            _audioBufferIndex = 0; 
        }

        public void Close()
        {
            Reset();
            PInvoke.waveOutClose(_hDevice);
        }

        public unsafe void Enqueue(byte[] data, uint length)
        {
            if (_audioBuffer == nint.Zero)
                throw new InvalidOperationException("You must first call Initialize!");

            uint waveHdrSize = (uint)sizeof(WAVEHDR);
            if ((length + _audioBufferIndex + waveHdrSize) > _audioBufferSize)
                _audioBufferIndex = 0;

            if ((length + waveHdrSize) < _audioBufferSize)
            {
                byte* pAudioBuffer = (byte*)_audioBuffer;
                byte* pAudioData = &pAudioBuffer[_audioBufferIndex + waveHdrSize];
                WAVEHDR* waveHdr = (WAVEHDR*)&pAudioBuffer[_audioBufferIndex];
                waveHdr->lpData = pAudioData;
                waveHdr->dwBufferLength = length;
                waveHdr->dwUser = nuint.Zero;
                waveHdr->dwFlags = 0;
                waveHdr->dwLoops = 0;

                Marshal.Copy(data, 0, (nint)pAudioData, (int)length);
                _audioBufferIndex += waveHdrSize + length;

                PInvoke.waveOutPrepareHeader(_hDevice, waveHdr, (uint)sizeof(WAVEHDR));
                PInvoke.waveOutWrite(_hDevice, waveHdr, (uint)sizeof(WAVEHDR));
                Interlocked.Increment(ref _queuedFrames);
            }
        }

        public static unsafe WaveOutDevice[] Enumerate()
        {
            uint deviceCount = PInvoke.waveOutGetNumDevs();
            List<WaveOutDevice> ret = new List<WaveOutDevice>();
            for (int i = 0; i < deviceCount; i++)
            {
                uint deviceID = (uint)i;
                WAVEOUTCAPS2W caps = new WAVEOUTCAPS2W();
                uint result = PInvoke.waveOutGetDevCapsW(deviceID, (WAVEOUTCAPSW*)&caps, (uint)Marshal.SizeOf<WAVEOUTCAPS2W>());
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
                    catch(Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                    }

                    ret.Add(
                        new WaveOutDevice(
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

        ~WaveOut()
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
