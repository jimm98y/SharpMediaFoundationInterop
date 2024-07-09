using SharpMediaFoundation.WPF;
using System.Windows;

namespace SharpMediaFoundation
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public VideoControlSource Source { get; set; } = new VideoControlSource("frag_bunny.mp4");

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
        }
    }
}