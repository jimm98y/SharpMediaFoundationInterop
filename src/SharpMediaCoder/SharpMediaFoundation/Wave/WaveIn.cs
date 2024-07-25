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

        // https://github.com/microsoft/CsWin32/issues/623
        private Delegate _callback; // hold on to the delegate so that it does not get garbage collected

        public unsafe void Initialize(uint samplesPerSecond, uint channels, uint bitsPerSample)
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
            PInvoke.waveInOpen(&device, WAVE_MAPPER, waveFormat, (nuint)Marshal.GetFunctionPointerForDelegate(_callback), nuint.Zero, MIDI_WAVE_OPEN_TYPE.CALLBACK_FUNCTION);
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
