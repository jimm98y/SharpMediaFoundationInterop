using SharpMp4;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaCoder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        WriteableBitmap _wb;
        System.Timers.Timer _timerDecoder;
        H264Decoder _h264Decoder;
        NV12toRGB _nv12Decoder;
        private ConcurrentQueue<IList<byte[]>> _sampleQueue = new ConcurrentQueue<IList<byte[]>>();
        private ConcurrentQueue<byte[]> _renderQueue = new ConcurrentQueue<byte[]>();
        private object _syncRoot = new object();
        Int32Rect _rect;

        int pw = 640;
        int ph = 368;
        int fps = 24;

        long _time = 0;
        long _lastTime = 0;

        byte[] nv12buffer;

        Stopwatch _stopwatch = new Stopwatch();

        public MainWindow()
        {
            InitializeComponent();

            _wb = new WriteableBitmap(
                pw,
                ph,
                96,
                96,
                PixelFormats.Bgr24,
                null);
            image.Source = _wb;

            _rect = new Int32Rect(0, 0, pw, ph);

            nv12buffer = new byte[pw * ph * 3];

            _timerDecoder = new System.Timers.Timer();
            _timerDecoder.Elapsed += OnTickDecoder;
            _timerDecoder.Interval = 1000d / fps;
            _timerDecoder.Start();
            
            _time = 0;
            _stopwatch.Start();

            this.Closing += MainWindow_Closing;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
        }

        private long _framecount = 0;

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (!_timerDecoder.Enabled)
                return;

            long elapsed = _stopwatch.ElapsedMilliseconds;

            if (elapsed - _lastTime >= _timerDecoder.Interval)
            {
                if (_renderQueue.TryDequeue(out byte[] decoded))
                {
                    _wb.Lock();
                    Marshal.Copy(decoded, 0, _wb.BackBuffer, pw * ph * 3);
                    _wb.AddDirtyRect(_rect);
                    _wb.Unlock();

                    ArrayPool<byte>.Shared.Return(decoded);
                    _lastTime = elapsed;
                }
            }
        }

        private void OnTickDecoder(object sender, ElapsedEventArgs e)
        {
            if (_h264Decoder == null)
            {
                lock (_syncRoot)
                {
                    if (_h264Decoder == null)
                    {
                        _h264Decoder = new H264Decoder(pw, ph, fps);
                        _nv12Decoder = new NV12toRGB(pw, ph, fps);
                    }
                }
            }

            while (_renderQueue.Count < 3 && _sampleQueue.Count > 0)
            {
                if (_sampleQueue.TryDequeue(out var au))
                {
                    foreach (var nalu in au)
                    {
                        if (_h264Decoder.ProcessInput(nalu, _time))
                        {
                            while (_h264Decoder.ProcessOutput(ref nv12buffer))
                            {
                                _nv12Decoder.ProcessInput(nv12buffer, _time);
                                
                                byte[] decoded = ArrayPool<byte>.Shared.Rent(pw * ph * 3);
                                _nv12Decoder.ProcessOutput(ref decoded);

                                _renderQueue.Enqueue(decoded);
                                _framecount++;
                            }
                        }
                    }
                    _time += (1000 * 10000 / fps);
                }
            }

            if (_sampleQueue.Count == 0)
            {
                lock (_syncRoot)
                {
                    if (_sampleQueue.Count == 0)
                    {
                        _timerDecoder.Stop();
                        LoadFileAsync("frag_bunny.mp4").Wait();
                        _time = 0;
                        Debug.WriteLine($"Total framecount: {_framecount}");
                        _framecount = 0;
                        _timerDecoder.Start();
                    }
                }
            }
        }

        private async Task LoadFileAsync(string fileName)
        {
            using (Stream fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
                {
                    var videoTrackBox = fmp4.FindVideoTracks().FirstOrDefault();
                    var audioTrackBox = fmp4.FindAudioTracks().FirstOrDefault();
                    var parsedMDAT = await fmp4.ParseMdatAsync();

                    var videoTrackId = fmp4.FindVideoTrackID().First();
                    var audioTrackId = fmp4.FindAudioTrackID().First();

                    foreach (var au in parsedMDAT[videoTrackId])
                    {
                        _sampleQueue.Enqueue(au);
                    }
                }
            }
        }
    }
}