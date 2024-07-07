using SharpMp4;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpMediaFoundation.WPF
{
    [TemplatePart(Name = "PART_image", Type = typeof(Image))]
    public class VideoControl : Control
    {
        private enum VideoCodecType
        {
            H264,
            H265
        }

        public string Path
        {
            get { return (string)GetValue(PathProperty); }
            set { SetValue(PathProperty, value); }
        }

        public static readonly DependencyProperty PathProperty =
            DependencyProperty.Register("Path", typeof(string), typeof(VideoControl), new PropertyMetadata("", OnPathPropertyChanged));

        private static async void OnPathPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sender = d as VideoControl;
            if(sender != null)
            {
                await sender.StartPlaying((string)e.NewValue);
            }
        }

        private Image _image;

        uint _width;
        uint _height;
        uint _fpsNom;
        uint _fpsDenom;
        VideoCodecType _codec;

        WriteableBitmap _wb;
        System.Timers.Timer _timerDecoder;
        IVideoTransform _videoDecoder;

        IVideoTransform _videoEncoder;


        NV12toRGB _nv12Decoder;
        private ConcurrentQueue<IList<byte[]>> _sampleQueue = new ConcurrentQueue<IList<byte[]>>();
        private ConcurrentQueue<byte[]> _renderQueue = new ConcurrentQueue<byte[]>();
        private SemaphoreSlim _semaphore = new SemaphoreSlim(1);
        Int32Rect _rect;
        long _time = 0;
        long _lastTime = 0;
        byte[] _nv12buffer;
        byte[] _encodedBuffer;
        Stopwatch _stopwatch = new Stopwatch();

        static VideoControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(VideoControl), new FrameworkPropertyMetadata(typeof(VideoControl)));
        }

        public VideoControl()
        {
            Loaded += VideoControl_Loaded;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void VideoControl_Loaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Closing += (s1, e1) =>
                {
                    CompositionTarget.Rendering -= CompositionTarget_Rendering;
                };
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            this._image = this.Template.FindName("PART_Image", this) as Image;
            this._image.RenderTransformOrigin = new Point(0.5, 0.5);

            // decoded video image is upside down (pixel rows are in the bitmap order) => flip it
            this._image.RenderTransform = new ScaleTransform(1, -1);

            this._image.Source = _wb;
        }


        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_timerDecoder == null  || !_timerDecoder.Enabled)
                return;

            long elapsed = _stopwatch.ElapsedMilliseconds;

            if (elapsed - _lastTime >= _timerDecoder.Interval)
            {
                if (_renderQueue.TryDequeue(out byte[] decoded))
                {
                    _wb.Lock();
                    Marshal.Copy(decoded, (int)(3 * (_videoDecoder.Height - _videoDecoder.OriginalHeight) * _videoDecoder.Width), _wb.BackBuffer, (int)(_width * _height * 3));
                    _wb.AddDirtyRect(_rect);
                    _wb.Unlock();

                    ArrayPool<byte>.Shared.Return(decoded);
                    _lastTime = elapsed;
                }
            }
        }

        private async void OnTickDecoder(object sender, ElapsedEventArgs e)
        {
            await _semaphore.WaitAsync();

            try
            { 
                if (_videoDecoder == null || _nv12Decoder == null)
                {
                    // decoders must be created on the same thread as the samples
                    if (_codec == VideoCodecType.H264)
                    {
                        _videoDecoder = new H264Decoder(_width, _height, _fpsNom, _fpsDenom);
                    }
                    else
                    {
                        _videoDecoder = new H265Decoder(_width, _height, _fpsNom, _fpsDenom);
                    }

                    _nv12Decoder = new NV12toRGB(_videoDecoder.Width, _videoDecoder.Height);

                    _videoEncoder = new H265Encoder(_width, _height, _fpsNom, _fpsDenom);
                }

                const int MIN_BUFFERED_SAMPLES = 3;
                while (_renderQueue.Count < MIN_BUFFERED_SAMPLES && _sampleQueue.Count > 0)
                {
                    if (_sampleQueue.TryDequeue(out var au))
                    {
                        foreach (var nalu in au)
                        {
                            if (_videoDecoder.ProcessInput(nalu, _time))
                            {
                                while (_videoDecoder.ProcessOutput(ref _nv12buffer, out _))
                                {
                                    if (_videoEncoder.ProcessInput(_nv12buffer, _time))
                                    {
                                        while (_videoEncoder.ProcessOutput(ref _encodedBuffer, out uint length))
                                        {
                                            var nnn = AnnexBUtils.ParseNalu(_encodedBuffer, length);
                                        }
                                    }

                                    _nv12Decoder.ProcessInput(_nv12buffer, _time);

                                    byte[] decoded = ArrayPool<byte>.Shared.Rent((int)(_width * _height * 3));
                                    _nv12Decoder.ProcessOutput(ref decoded, out _);

                                    _renderQueue.Enqueue(decoded);
                                }
                            }
                        }
                        _time += 10000 * 1000 / (_fpsNom / _fpsDenom); // 100ns units
                    }
                }

                if (_sampleQueue.Count == 0)
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await StartPlaying(Path);
                    });
                }
            }
            finally
            {
                _semaphore.Release();
            }            
        }

        private async Task StartPlaying(string path)
        {
            if (_sampleQueue.Count == 0)
            {
                try
                {
                    _timerDecoder?.Stop();
                    await LoadFileAsync(path);

                    if (_timerDecoder == null)
                    {
                        _wb = new WriteableBitmap(
                            (int)_width,
                            (int)_height,
                            96,
                            96,
                            PixelFormats.Bgr24,
                            null);
                        _rect = new Int32Rect(0, 0, (int)_width, (int)_height);
                        _nv12buffer = new byte[_width * _height * 3];
                        _encodedBuffer = new byte[_width * _height * 3 / 2];
                        _timerDecoder = new System.Timers.Timer();
                        _timerDecoder.Elapsed += OnTickDecoder;
                        _timerDecoder.Interval = 1000 * _fpsDenom / _fpsNom;
                        _timerDecoder.Start();
                        _time = 0;
                        _stopwatch.Start();
                    }
                    else
                    {
                        _time = 0;
                        _timerDecoder.Start();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }
        }

        private async Task LoadFileAsync(string fileName)
        {
            _sampleQueue.Clear();
            using (Stream fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
                {
                    var videoTrackBox = fmp4.FindVideoTracks().FirstOrDefault();
                    var audioTrackBox = fmp4.FindAudioTracks().FirstOrDefault();
                    var parsedMDAT = await fmp4.ParseMdatAsync();

                    var videoTrackId = fmp4.FindVideoTrackID().First();
                    var audioTrackId = fmp4.FindAudioTrackID().FirstOrDefault();

                    var vsbox = videoTrackBox.GetMdia().GetMinf().GetStbl()
                        .GetStsd()
                        .Children.Single((Mp4Box x) => x is VisualSampleEntryBox) as VisualSampleEntryBox;
                    _width = vsbox.Width;
                    _height = vsbox.Height;
                    _fpsNom = fmp4.CalculateTimescale(videoTrackBox);
                    _fpsDenom = fmp4.CalculateSampleDuration(videoTrackBox);
                    if(vsbox.Children.FirstOrDefault(x => x is AvcConfigurationBox) != null)
                    {
                        _codec = VideoCodecType.H264;
                    }
                    else if(vsbox.Children.FirstOrDefault(x => x is HevcConfigurationBox) != null)
                    {
                        _codec = VideoCodecType.H265;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }

                    foreach (var au in parsedMDAT[videoTrackId])
                    {
                        _sampleQueue.Enqueue(au);
                    }
                }
            }
        }
    }
}
