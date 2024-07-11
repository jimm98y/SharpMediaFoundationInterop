using SharpMediaFoundation.WPF;
using System.Windows;

namespace SharpMediaFoundation
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public IVideoControlSource Source { get; set; } = new RtspControlSource("rtsp://127.0.0.1:8554", "admin", "password");

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
        }
    }
}