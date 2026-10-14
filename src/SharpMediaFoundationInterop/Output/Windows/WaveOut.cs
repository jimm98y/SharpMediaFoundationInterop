using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Media;
using Windows.Win32.Media.Audio;

namespace SharpMediaFoundationInterop.Output
{   
    [SupportedOSPlatform("windows10.0.17763.0")]
    public class WaveOut : IAudioOutput
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

        /// <summary>The device it plays on: <see cref="WAVE_MAPPER"/>, the system's default, unless one is given.</summary>
        public uint DeviceID { get; private set; } = WAVE_MAPPER;

        public uint SampleRate { get; private set; } = 48000;
        public uint Channels { get; private set; } = 2;
        public uint BitsPerSample { get; private set; } = 16;

        private int _queuedFrames = 0;
        public int QueuedFrames {  get { return _queuedFrames; } }

        private const int _audioBufferSize = 1024 * 1024; 
        private nint _audioBuffer = nint.Zero;   
        private uint _audioBufferIndex = 0;

        private bool _disposedValue;

        public event EventHandler<EventArgs> OnPlaybackCompleted;

        // https://github.com/microsoft/CsWin32/issues/623
        private Delegate _callback; // hold on to the delegate so that it does not get garbage collected

        public WaveOut()
        { }

        /// <summary>Of the device and the format <see cref="Initialize()"/> opens it for.</summary>
        public WaveOut(uint deviceID, uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            DeviceID = deviceID;
            SampleRate = samplesPerSecond;
            Channels = channels;
            BitsPerSample = bitsPerSample;
        }

        /// <summary>Opens the device and the format it was made for.</summary>
        public void Initialize()
        {
            Initialize(DeviceID, SampleRate, Channels, BitsPerSample);
        }

        public void Initialize(uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Initialize(WAVE_MAPPER, samplesPerSecond, channels, bitsPerSample);
        }

        public unsafe void Initialize(uint deviceID, uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Close();

            DeviceID = deviceID;
            SampleRate = samplesPerSecond;
            Channels = channels;
            BitsPerSample = bitsPerSample;

            if (_audioBuffer == nint.Zero)
            { 
                _audioBuffer = Marshal.AllocHGlobal(_audioBufferSize);
            }

            _callback = DoneCallback;

            // Of more than two channels, or more than 16 bits, WAVEFORMATEXTENSIBLE: which speaker each channel is for - the
            // channels in WAVE's order, as Windows' decoders hand them out - and the bits that are of the samples. Without
            // it the device takes no more than stereo of 16 bits; with it, Windows plays 5.1 on 5.1 speakers, and mixes
            // it down to what the speakers are.
            var format = new WaveFormatExtensible
            {
                wFormatTag = channels > 2 || bitsPerSample > 16 ? WAVE_FORMAT_EXTENSIBLE : WAVE_FORMAT_PCM,
                nChannels = (ushort)channels,
                nSamplesPerSec = samplesPerSecond,
                nAvgBytesPerSec = samplesPerSecond * (bitsPerSample / 8) * channels,
                nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
                wBitsPerSample = (ushort)bitsPerSample,
            };
            if (format.wFormatTag == WAVE_FORMAT_EXTENSIBLE)
            {
                format.cbSize = 22;
                format.wValidBitsPerSample = (ushort)bitsPerSample;
                format.dwChannelMask = ChannelMask(channels);
                format.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
            }

            // a device refused leaves no handle: none is kept, and nothing is played - not a handle of whatever was there
            HWAVEOUT device = default;
            uint result = PInvoke.waveOutOpen(&device, deviceID, (WAVEFORMATEX*)&format, (nuint)Marshal.GetFunctionPointerForDelegate(_callback), nuint.Zero, MIDI_WAVE_OPEN_TYPE.CALLBACK_FUNCTION);
            if (result != MMSYSERR_NOERROR)
            {
                device = default;
                if (Log.ErrorEnabled)
                    Log.Error($"The sound device refused {channels} channels of {bitsPerSample} bits at {samplesPerSecond} Hz: MMRESULT {result}");
            }
            this._hDevice = device;
            Reset();
        }

        /// <summary>Whether a device is open: not where it refused the format.</summary>
        public bool IsOpen => !_hDevice.Equals(default(HWAVEOUT));

        private const ushort WAVE_FORMAT_PCM = 1;
        private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
        private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");

        /// <summary>WAVEFORMATEX, then what WAVE_FORMAT_EXTENSIBLE adds: the valid bits, the speakers, the subformat.</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormatExtensible
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
        }

        /// <summary>
        /// The speakers of so many channels in WAVE's order, as dwChannelMask has them: mono the front centre; stereo the
        /// front left and right; 3.0, quad, 5.0, 5.1 and 6.1 of the back speakers, as Windows' 5.1 is; 7.1 with the side
        /// ones too. More than 8, the first so many of the mask's bits.
        /// </summary>
        public static uint ChannelMask(uint channels) => channels switch
        {
            1 => 0x4,          // FC
            2 => 0x3,          // FL FR
            3 => 0x7,          // FL FR FC
            4 => 0x33,         // FL FR BL BR
            5 => 0x37,         // FL FR FC BL BR
            6 => 0x3F,         // FL FR FC LFE BL BR
            7 => 0x13F,        // FL FR FC LFE BL BR BC
            8 => 0x63F,        // FL FR FC LFE BL BR SL SR
            _ => channels >= 32 ? uint.MaxValue : (1u << (int)channels) - 1,
        };

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
            if (!IsOpen)
                return 0;
            MMTIME time = new MMTIME();
            time.wType = TIME_BYTES;
            PInvoke.waveOutGetPosition(_hDevice, &time, (uint)sizeof(MMTIME));
            if (time.wType != TIME_BYTES)
                throw new Exception("WaveOut device does not support reading time in bytes");
            return time.u.sample;
        }

        public void Reset()
        {
            if (IsOpen)
                PInvoke.waveOutReset(_hDevice);
            Interlocked.Exchange(ref _queuedFrames, 0);
            _audioBufferIndex = 0; 
        }

        /// <summary>Stops playing where it is, keeping what is queued: <see cref="Resume"/> plays on from there.</summary>
        public void Pause()
        {
            if (IsOpen)
                PInvoke.waveOutPause(_hDevice);
        }

        public void Resume()
        {
            if (IsOpen)
                PInvoke.waveOutRestart(_hDevice);
        }

        public void Close()
        {
            Reset(); // waveOutReset must be called before waveOutClose
            if (IsOpen)
                PInvoke.waveOutClose(_hDevice);
            _hDevice = default;
        }

        public unsafe void Enqueue(byte[] data, uint length)
        {
            if (_audioBuffer == nint.Zero)
                throw new InvalidOperationException("You must first call Initialize!");
            if (!IsOpen)
                return; // a device that refused the format: nothing plays

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
                        if (Log.ErrorEnabled) Log.Error(ex.Message, ex);
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
