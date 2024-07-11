using SharpMediaFoundation.WPF;
using System.Windows;

namespace SharpMediaFoundation
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public IVideoSource Source { get { return new VideoSource("frag_bunny.mp4"); } }

        public MainWindow()
        {
            InitializeComponent();
            this.DataContext = this;
        }
    }
}