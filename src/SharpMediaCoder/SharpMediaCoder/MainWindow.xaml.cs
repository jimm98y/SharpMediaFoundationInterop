using SharpMp4;
using System.IO;
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
        
        int pw = 640;
        int ph = 368;
        int fps = 24;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            _wb = new WriteableBitmap(
                pw,
                ph,
                96,
                96,
                PixelFormats.Bgr24,
                null);
            image.Source = _wb;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string fileName = "frag_bunny.mp4";

            H264Decoder decoder = new H264Decoder(pw, ph, fps);

            using (Stream fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
                {
                    var videoTrackBox = fmp4.FindVideoTracks().FirstOrDefault();
                    var audioTrackBox = fmp4.FindAudioTracks().FirstOrDefault();
                    var parsedMDAT = await fmp4.ParseMdatAsync();

                    var videoTrackId = fmp4.FindVideoTrackID().First();
                    var audioTrackId = fmp4.FindAudioTrackID().First();

                    bool keyframe = false;
                    foreach (var au in parsedMDAT[videoTrackId])
                    {
                        keyframe = true;
                        foreach (var nalu in au)
                        {
                            byte[] decoded = decoder.Process(nalu, keyframe);
                            keyframe = false;
                            if (decoded != null)
                            {
                                _wb.WritePixels(new Int32Rect(0, 0, pw, ph), decoded, pw * 3, 0);
                                await Task.Delay((int)(1000d / fps));
                            }
                        }
                    }
                }
            }
        }
    }
}