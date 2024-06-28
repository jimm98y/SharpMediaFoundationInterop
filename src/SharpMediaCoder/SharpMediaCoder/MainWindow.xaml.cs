using SharpMp4;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SharpMediaCoder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        WriteableBitmap _wb;
        
        int pw = 640;
        int ph = 360;

        byte[] rgbData;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            _wb = new WriteableBitmap(
                pw,
                ph,
                96,
                96,
                PixelFormats.Bgra32,
                null);
            rgbData = new byte[pw * ph * 4];
            image.Source = _wb;
        }

        private static unsafe void NV12ToRGBManaged(byte[] YUVData, byte[] RGBData, int width, int height, int heightPad = 8)
        {
            fixed (byte* rgbbuffer = RGBData, yuvbuffer = YUVData)
            {
                var rgbbuff = rgbbuffer;
                var yuvbuff = yuvbuffer;

                for (int y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var vIndex = width * (height + heightPad) + ((y >> 1) * width) + (x & ~1);
                        var yIndex = y * width + x;

                        //// https://msdn.microsoft.com/en-us/library/windows/desktop/dd206750(v=vs.85).aspx
                        //// https://en.wikipedia.org/wiki/YUV
                        var c = *(yuvbuff + yIndex) - 16;
                        var d = *(yuvbuff + vIndex) - 128;
                        var e = *(yuvbuff + vIndex + 1) - 128;
                        c = c < 0 ? 0 : c;

                        var r = ((298 * c) + (409 * e) + 128) >> 8;
                        var g = ((298 * c) - (100 * d) - (208 * e) + 128) >> 8;
                        var b = ((298 * c) + (516 * d) + 128) >> 8;
                        *rgbbuff++ = (byte)int.Clamp(b, 0, 255);
                        *rgbbuff++ = (byte)int.Clamp(g, 0, 255);
                        *rgbbuff++ = (byte)int.Clamp(r, 0, 255);
                        *rgbbuff++ = 255;
                    }
                }
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string fileName = "frag_bunny.mp4";

            H264Decoder decoder = new H264Decoder();

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
                            byte[] decoded = decoder.Process(nalu);
                            if (decoded != null)
                            {
                                NV12ToRGBManaged(decoded, rgbData, pw, ph);
                                _wb.WritePixels(new Int32Rect(0, 0, pw, ph), rgbData, pw * 4, 0);
                                await Task.Delay(10);
                            }
                            else
                            {
                                Debug.WriteLine("No frame");
                            }
                        }
                    }
                }
            }
        }
    }
}