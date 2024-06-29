using SharpMp4;
using System.Collections.Concurrent;
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
        System.Timers.Timer _timer;
        H264Decoder _decoder;
        private ConcurrentQueue<byte[]> _sampleQueue = new ConcurrentQueue<byte[]>();
        private object _syncRoot = new object();
        Int32Rect _rect;

        int pw = 640;
        int ph = 368;
        int fps = 23;

        byte[] _decoded;

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

            _timer = new System.Timers.Timer();
            _timer.Elapsed += OnTick;
            _timer.Interval = 1000d / fps;
            _timer.Start();
        }

        private void OnTick(object sender, ElapsedEventArgs e)
        {
            var ticks = DateTime.Now.Ticks;
            if (_decoder == null)
            {
                lock (_syncRoot)
                {
                    if(_decoder == null)
                        _decoder = new H264Decoder(pw, ph, fps);
                }
            }

            while (_sampleQueue.Count > 0)
            {
                if (_sampleQueue.TryDequeue(out var nalu))
                {
                    if(_decoder.Process(nalu, ticks, ref _decoded))
                    { 
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                _wb.Lock();
                                _wb.WritePixels(_rect, _decoded, pw * 3, 0);
                                _wb.AddDirtyRect(_rect);
                                _wb.Unlock();
                            }
                            catch(Exception ex) { }
                        });
                        break;
                    }
                }
            }

            if(_sampleQueue.Count == 0)
            {
                lock (_syncRoot)
                {
                    if (_sampleQueue.Count == 0)
                    {
                        _timer.Stop();
                        LoadFileAsync("frag_bunny.mp4").Wait();
                        _timer.Start();
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
                        foreach (var nalu in au)
                        {
                            _sampleQueue.Enqueue(nalu);
                        }
                    }
                }
            }
        }
    }
}