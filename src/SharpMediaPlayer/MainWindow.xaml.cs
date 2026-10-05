using Microsoft.Win32;
using SharpMediaFoundationInterop.WPF;
using System;
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

        private const string VideoFilter = "Videos|*.mp4;*.mov;*.m4v;*.3gp|All files|*.*";

        // what the File tab plays: one video, or a stereo pair's two
        private string[] _files;
        private VideoControlBase _fileControl;

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;

            // a video, or a stereo pair - left, then right - given on the command line
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
                Loaded += (s, e) => PlayFiles(args[1..Math.Min(args.Length, 3)]);
        }

        private void OpenVideo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = VideoFilter, Title = "Open a video" };
            if (dialog.ShowDialog(this) == true)
                PlayFiles(new[] { dialog.FileName });
        }

        private void OpenStereo_Click(object sender, RoutedEventArgs e)
        {
            var left = new OpenFileDialog { Filter = VideoFilter, Title = "Open the left eye's video" };
            if (left.ShowDialog(this) != true)
                return;
            var right = new OpenFileDialog { Filter = VideoFilter, Title = "Open the right eye's video", InitialDirectory = System.IO.Path.GetDirectoryName(left.FileName) };
            if (right.ShowDialog(this) == true)
                PlayFiles(new[] { left.FileName, right.FileName });
        }

        private void UseDirect3D_Click(object sender, RoutedEventArgs e)
        {
            if (_files != null)
                PlayFiles(_files);
        }

        /// <summary>Plays a video, or two as a stereo pair side by side, in the File tab, in the control chosen.</summary>
        private void PlayFiles(string[] files)
        {
            _files = files;
            _fileControl?.Dispose();

            IVideoSource source = files.Length == 2
                ? new StereoVideoSource(new VideoFileSource(files[0]), new VideoFileSource(files[1]))
                : new VideoFileSource(files[0]);
            _fileControl = UseDirect3D.IsChecked == true ? new VideoControlD3D() : new VideoControl();
            _fileControl.Looping = true;
            _fileControl.Source = source;
            FileHost.Content = _fileControl;
            Tabs.SelectedItem = FileTab;
            Title = $"SharpMediaPlayer - {string.Join(" + ", Array.ConvertAll(files, System.IO.Path.GetFileName))}";
        }
    }
}
