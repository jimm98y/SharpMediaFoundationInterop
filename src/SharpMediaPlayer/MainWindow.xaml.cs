using SharpMediaFoundationInterop.WPF;
using System.Windows;

namespace SharpMediaFoundationInterop
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public IVideoSource ScreenSource { get { return new ScreenSource(); } }
        public IVideoSource CameraSource { get { return new CameraSource(); } }
        public IVideoSource MP4Source { get { return new VideoFileSource("frag_bunny.mp4"); } } // H264
        public IVideoSource HeicSource { get { return new ImageFileSource("test.heic"); } } // heic
        public IVideoSource RtspSource { get { return new RtspSource("rtsp://127.0.0.1:8554", "admin", "password"); } }

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            SharpH26X.Log.SinkDebug = (o, e) => { };
            SharpH26X.Log.SinkInfo = (o, e) => { };
            SharpAV1.Log.SinkInfo = (o, e) => { };
            SharpAV1.Log.SinkDebug = (o, e) => { };
            SharpISOBMFF.Log.SinkInfo = (o, e) => { };
            SharpISOBMFF.Log.SinkDebug = (o, e) => { };
            SharpMP4.Log.SinkInfo = (o, e) => { };
            SharpMP4.Log.SinkDebug = (o, e) => { };
        }
    }
}