using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace SharpMediaFoundation.Output
{
    public unsafe class WaveOut : IDisposable
    {
        public const int BUFFER_DONE = 0x3BD;

        private HWAVEOUT _hDevice;

        private uint _queuedFrames = 0;
        private nint _completedFrames = nint.Zero; 

        private const int _audioBufferSize = 1024 * 1024; 
        private nint _audioBuffer = nint.Zero;   
        private uint _audioBufferIndex = 0;

        private bool _disposedValue;

        public unsafe void Initialize(uint samplesPerSecond, uint channels, uint bitsPerSample)
        {
            Close();

            if (_audioBuffer == nint.Zero)
            { 
                _audioBuffer = Marshal.AllocHGlobal(_audioBufferSize); 
                _completedFrames = Marshal.AllocHGlobal(4); 
            }

            WAVEFORMATEX waveFormat = new WAVEFORMATEX();  
            waveFormat.nAvgBytesPerSec = samplesPerSecond * (bitsPerSample / 8) * channels;
            waveFormat.nBlockAlign = (ushort)(channels * (bitsPerSample / 8));
            waveFormat.nChannels = (ushort)channels;
            waveFormat.nSamplesPerSec = samplesPerSecond;
            waveFormat.wBitsPerSample = (ushort)bitsPerSample;
            waveFormat.cbSize = 0;
            waveFormat.wFormatTag = 1; // pcm

            HWAVEOUT device;
            const uint WAVE_MAPPER = unchecked((uint)-1);
            nint woDone = Marshal.GetFunctionPointerForDelegate(DoneCallback);
            PInvoke.waveOutOpen(&device, WAVE_MAPPER, waveFormat, (nuint)woDone, (nuint)_completedFrames, MIDI_WAVE_OPEN_TYPE.CALLBACK_FUNCTION); 
            this._hDevice = device;
            Reset();
        }

        unsafe static void DoneCallback(HWAVEOUT* dev, uint uMsg, uint* dwUser, uint dwParam1, uint dwParam2)
        {
            if (uMsg == BUFFER_DONE && dwUser != null)
            {
                (*dwUser)++;
            }
        }

        public unsafe void Reset()
        {
            PInvoke.waveOutRestart(_hDevice);
            _queuedFrames = 0;
            if (_completedFrames != nint.Zero)
            {
                uint* i = (uint*)_completedFrames.ToPointer();
                if(i != null)
                {
                    *i = 0;
                }
            }
            _audioBufferIndex = 0; 
        }

        public void Close()
        {
            Reset();
            PInvoke.waveOutClose(_hDevice);
        }

        public unsafe void Play(byte[] data, uint length)
        {
            if (_audioBuffer != nint.Zero)
            {
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

                    PInvoke.waveOutPrepareHeader(_hDevice, ref *waveHdr, (uint)sizeof(WAVEHDR));
                    PInvoke.waveOutWrite(_hDevice, ref *waveHdr, (uint)sizeof(WAVEHDR));
                    _queuedFrames++;
                }
            }
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

                if (_completedFrames != nint.Zero)
                {
                    Marshal.FreeHGlobal(_completedFrames);
                    _completedFrames = nint.Zero;
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
