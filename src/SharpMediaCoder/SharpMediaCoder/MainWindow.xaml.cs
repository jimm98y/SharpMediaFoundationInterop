using SharpMp4;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
        H264Decoder _decoder;
        private ConcurrentQueue<IList<byte[]>> _sampleQueue = new ConcurrentQueue<IList<byte[]>>();
        private ConcurrentQueue<byte[]> _renderQueue = new ConcurrentQueue<byte[]>();
        private object _syncRoot = new object();
        Int32Rect _rect;

        int pw = 640;
        int ph = 368;
        int fps = 24;

        byte[] _decoded;
        long _time = 0;

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
            _decoded = new byte[pw * ph * 3];

            _timerDecoder = new System.Timers.Timer();
            _timerDecoder.Elapsed += OnTickDecoder;
            _timerDecoder.Interval = 1000d / fps;
            _timerDecoder.Start();
            
            _time = 0;
            _stopwatch.Start();

            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        long _lastTime = 0;

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            long elapsed = _stopwatch.ElapsedMilliseconds;

            if (elapsed - _lastTime >= _timerDecoder.Interval)
            {
                //Debug.WriteLine($"Frame: {elapsed - _lastTime}");

                if (_renderQueue.TryDequeue(out byte[] image))
                {
                    _wb.WritePixels(_rect, image, pw * 3, 0);
                    _lastTime = _stopwatch.ElapsedMilliseconds;
                }
                else
                {
                    Debug.WriteLine("no frame");
                }
            }
        }

        private void OnTickDecoder(object sender, ElapsedEventArgs e)
        {
            if (_decoder == null)
            {
                lock (_syncRoot)
                {
                    if(_decoder == null)
                        _decoder = new H264Decoder(pw, ph, fps);
                }
            }

            while (_renderQueue.Count < 10 && _sampleQueue.Count > 0)
            {
                if (_sampleQueue.TryDequeue(out var au))
                {
                    foreach (var nalu in au)
                    {
                        if (_decoder.Process(nalu, _time, ref _decoded))
                        {
                            _renderQueue.Enqueue(_decoded.ToArray());
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