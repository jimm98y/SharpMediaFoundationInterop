using SharpMediaFoundationInterop.WPF;
using System.Windows;

namespace SharpMediaFoundationInterop
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        //public IVideoSource Source { get { return new ScreenSource(); } }
        //public IVideoSource Source { get { return new CameraSource(); } }
        //public IVideoSource Source { get { return new ImageFileSource("test.heic"); } } // heic
        //public IVideoSource Source { get { return new VideoFileSource("frag_bunny.mp4"); } } // H264
        //public IVideoSource Source { get { return new VideoFileSource("C:\\Temp\\002.mp4"); } } // AV1
        public IVideoSource Source { get { return new VideoFileSource("bunny.mp4"); } } // H264
        //public IVideoSource Source { get { return new VideoFileSource("test1.mp4"); } } // H265
        //public IVideoSource Source { get { return new VideoFileSource("fragmented_320_1000.mp4"); } } // H264
        //public IVideoSource Source { get { return new RtspSource("rtsp://127.0.0.1:8554", "admin", "password"); } }

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            SharpISOBMFF.Log.SinkDebug = (m, e) => { };
            SharpISOBMFF.Log.SinkInfo = (m, e) => { };
        }
    }
}