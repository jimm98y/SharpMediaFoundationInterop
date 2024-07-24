using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace SharpMediaFoundation.Wave
{
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
        public const uint WAVE_MAPPER = unchecked((uint)-1);

        private HWAVEIN _hDevice;

        private const int _audioBufferSize = 1024 * 1024;
        private nint _audioBuffer = nint.Zero;
        private uint _audioBufferIndex = 0;

        public event EventHandler<WaveInEventArgs> FrameReceived;

        const int NUM_BUF = 3;

        private bool _disposedValue;

        public unsafe void Initialize(uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Close();

            if (_audioBuffer == nint.Zero)
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
            nint woDone = Marshal.GetFunctionPointerForDelegate(DoneCallback);
            PInvoke.waveInOpen(&device, WAVE_MAPPER, waveFormat, (nuint)woDone, nuint.Zero, MIDI_WAVE_OPEN_TYPE.CALLBACK_FUNCTION);
            this._hDevice = device;

            uint waveHdrSize = (uint)sizeof(WAVEHDR);
            byte* pAudioBuffer = (byte*)_audioBuffer;

            for (int i = 0; i < NUM_BUF; i++)
            {
                byte* pAudioData = &pAudioBuffer[_audioBufferIndex + waveHdrSize];
                WAVEHDR* waveHdr = (WAVEHDR*)&pAudioBuffer[_audioBufferIndex];
                waveHdr->lpData = pAudioData;
                waveHdr->dwBufferLength = waveFormat.nSamplesPerSec * waveFormat.nChannels * waveFormat.wBitsPerSample / 8;
                waveHdr->dwUser = nuint.Zero;
                waveHdr->dwFlags = 0;
                waveHdr->dwLoops = 0;
                PInvoke.waveInPrepareHeader(_hDevice, ref *waveHdr, (uint)sizeof(WAVEHDR));
                _audioBufferIndex += waveHdrSize + waveHdr->dwBufferLength;
            }

            _audioBufferIndex = 0;
            PInvoke.waveInAddBuffer(_hDevice, ref *(WAVEHDR*)&pAudioBuffer[_audioBufferIndex], (uint)sizeof(WAVEHDR));
            
            Reset();
        }

        private unsafe void DoneCallback(HWAVEIN* dev, uint uMsg, uint* dwUser, uint dwParam1, uint dwParam2)
        {
            if (uMsg == MM_WIM_DATA)
            {
                uint waveHdrSize = (uint)sizeof(WAVEHDR);
                byte* pAudioBuffer = (byte*)_audioBuffer;
                byte* pAudioData = &pAudioBuffer[_audioBufferIndex + waveHdrSize];
                WAVEHDR* waveHdr = (WAVEHDR*)&pAudioBuffer[_audioBufferIndex];

                byte[] dest = new byte[waveHdr->dwBufferLength];
                Marshal.Copy((nint)pAudioData, dest, 0, dest.Length);
                FrameReceived?.Invoke(this, new WaveInEventArgs(dest));

                _audioBufferIndex = (_audioBufferIndex + waveHdr->dwBufferLength + waveHdrSize) % (NUM_BUF * (waveHdr->dwBufferLength + waveHdrSize));

                PInvoke.waveInAddBuffer(_hDevice, ref *(WAVEHDR*)&pAudioBuffer[_audioBufferIndex], (uint)sizeof(WAVEHDR));
            }
        }

        public void Reset()
        {
            //PInvoke.waveInStop(_hDevice);
            _audioBufferIndex = 0;
            PInvoke.waveInStart(_hDevice);
        }

        public void Close()
        {
            Reset();
            PInvoke.waveInClose(_hDevice);
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
