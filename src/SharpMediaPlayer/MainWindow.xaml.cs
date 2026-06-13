using System.IO;
using System.Windows;

namespace SharpMediaFoundationInterop
{
    public partial class MainWindow : Window
    {
        public Mp4VideoSource MP4Source { get; }

        public MainWindow()
        {
            string filePath = File.Exists("test1.mp4") ? "test1.mp4" : "frag_bunny.mp4";
            MP4Source = new Mp4VideoSource(filePath);
            MP4Source.Initialize();

            InitializeComponent();
            DataContext = this;
        }
    }
}
