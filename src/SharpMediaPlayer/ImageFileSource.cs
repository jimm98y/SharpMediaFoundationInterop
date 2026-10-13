using SharpAV1;
using SharpH264;
using SharpH265;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using SharpMP4.Readers;
using SharpMP4.Tracks;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A HEIF image - a HEIC's, an AVIF's - as a video of one frame: decoded whole as it is opened, a grid's tiles each
    /// decoded and put in their place, made into BGRA as its colour says, then turned, mirrored and cropped as its
    /// properties say. Of a stereo pair - a spatial photo's 'ster' group - both eyes' images, side by side.
    /// </summary>
    /// <remarks>
    /// Seekable, of no length: the picture is handed out once, and again at each seek - which a paused control asks for to
    /// draw it anew, as an eye is chosen.
    /// </remarks>
    public class ImageFileSource : ISeekableVideoSource
    {
        private readonly string _path;

        // the picture, BGRA top-down, handed out as it is: nothing draws into a frame
        private byte[] _picture;
        private bool _handedOut;

        public VideoInfo VideoInfo { get; private set; }

        public ImageFileSource(string path)
        {
            this._path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>Whether a file is one of the images this shows, by its extension.</summary>
        public static bool IsImage(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".heic" || extension == ".heif" || extension == ".hif" || extension == ".avif";
        }

        public Task InitializeAsync()
        {
            if (VideoInfo == null)
                Load();
            _handedOut = false;
            return Task.CompletedTask;
        }

        /// <summary>BGRA alone: it is converted here, of the matrix and the range the image says.</summary>
        public bool TrySetOutputFormat(PixelFormat format) => format == PixelFormat.BGRA32;

        public byte[] GetVideoSample(out long timestamp)
        {
            timestamp = -1;
            if (VideoInfo == null)
                return null;

            lock (_seekLock)
            {
                if (_seekPending)
                {
                    _seekPending = false;
                    _handedOut = false;
                    Interlocked.Exchange(ref _request, _requested);
                }
            }

            if (_handedOut)
                return null; // shown: the end, until a seek
            _handedOut = true;
            timestamp = 0;
            return _picture;
        }

        public void ReturnVideoSample(byte[] sample)
        {
            // the one picture, handed out again at each seek
        }

        #region Seeking

        private readonly object _seekLock = new object();
        private bool _seekPending;
        private long _requested;
        private long _request;

        public bool CanSeek => true;
        public long Duration => 0;
        public long StartTime => 0;
        public int Rate { get; private set; } = 1;
        public long Request => Interlocked.Read(ref _request);

        public long Seek(long time, int rate)
        {
            lock (_seekLock)
            {
                Rate = rate;
                _seekPending = true;
                return ++_requested;
            }
        }

        #endregion

        private void Load()
        {
            using var fs = new BufferedStream(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read));
            var container = new Container();
            container.Read(new IsoStream(fs));

            using var reader = new ImageReader();
            reader.Parse(container);

            // a spatial photo's two eyes, left then right, where the file groups them
            var stereo = reader.Groups.OfType<StereoEntityGroupBox>().FirstOrDefault(x => x.EntityId?.Length == 2);
            Bgra picture;
            var info = new VideoInfo();
            if (stereo != null)
            {
                var left = DecodeItem(reader, stereo.EntityId[0]);
                var right = DecodeItem(reader, stereo.EntityId[1]);
                picture = Bgra.SideBySide(left, right);
                info.StereoLayout = StereoLayout.SideBySide;
            }
            else
            {
                picture = DecodeItem(reader, reader.PrimaryItemID);
            }

            _picture = picture.Pixels;
            info.Width = info.OriginalWidth = (uint)picture.Width;
            info.Height = info.OriginalHeight = (uint)picture.Height;
            info.FpsNom = 1;
            info.FpsDenom = 1;
            info.PixelFormat = PixelFormat.BGRA32;
            VideoInfo = info;
        }

        private static readonly uint GridType = IsoStream.FromFourCC("grid");

        /// <summary>An item's image: coded, or a grid of coded tiles; then as its transformative properties say, in their order.</summary>
        private static Bgra DecodeItem(ImageReader reader, uint itemID)
        {
            var properties = reader.PropertiesOf(itemID);

            Bgra image;
            if (reader.ItemTypeOf(itemID) == GridType)
            {
                var grid = reader.ReadGrid(itemID);
                var tiles = reader.DerivedFrom(itemID);
                if (tiles.Count != grid.Rows * grid.Columns)
                    throw new InvalidDataException($"The grid {itemID} of {grid.Rows}x{grid.Columns} has {tiles.Count} tiles.");

                // The colour of the grid, where it says it; else of its tiles, as they are all coded alike. The tiles' own
                // transformations, which there should be none of, are not applied.
                image = DecodeTiles(reader, tiles, grid.Columns, (int)grid.OutputWidth, (int)grid.OutputHeight,
                    properties.OfType<ColourInformationBox>().FirstOrDefault(IsNclx) ?? reader.PropertiesOf(tiles[0]).OfType<ColourInformationBox>().FirstOrDefault(IsNclx));
            }
            else
            {
                image = DecodeTiles(reader, new[] { itemID }, 1, -1, -1, properties.OfType<ColourInformationBox>().FirstOrDefault(IsNclx));
            }

            foreach (var property in properties)
            {
                switch (property)
                {
                    case CleanApertureBox clap:
                        image = image.Crop(clap);
                        break;
                    case ImageRotation irot:
                        image = image.RotateAnticlockwise(irot.Angle & 3);
                        break;
                    case ImageMirror imir:
                        image = image.Mirror(imir.Axis);
                        break;
                }
            }
            return image;
        }

        private static bool IsNclx(ColourInformationBox colr) => colr.ColourType == IsoStream.FromFourCC("nclx");

        /// <summary>
        /// Coded items decoded, each in its place of a grid of so many columns, and made into BGRA of the size given - -1 for
        /// the size of the one item there is.
        /// </summary>
        private static Bgra DecodeTiles(ImageReader reader, IReadOnlyList<uint> tiles, int columns, int width, int height, ColourInformationBox colr)
        {
            using var track = reader.CreateTrack(tiles[0])
                ?? throw new NotSupportedException($"Item {tiles[0]} is of no codec that can be read.");

            // the size of a tile, as its stream says it
            VideoCodec codec;
            int tileWidth, tileHeight;
            switch (track)
            {
                case H265Track hevc:
                    codec = VideoCodec.H265;
                    var hevcSize = hevc.Sps.First().Value.CalculateDimensions();
                    (tileWidth, tileHeight) = ((int)hevcSize.Width, (int)hevcSize.Height);
                    break;
                case H264Track avc:
                    codec = VideoCodec.H264;
                    var avcSize = avc.Sps.First().Value.CalculateDimensions();
                    (tileWidth, tileHeight) = ((int)avcSize.Width, (int)avcSize.Height);
                    break;
                case AV1Track av1:
                    codec = VideoCodec.AV1;
                    var av1Size = av1.SequenceHeaderObu.CalculateDimensions();
                    (tileWidth, tileHeight) = ((int)av1Size.Width, (int)av1Size.Height);
                    break;
                default:
                    throw new NotSupportedException($"Images of {track.GetType().Name} are not supported.");
            }

            if (width < 0)
            {
                width = tileWidth;
                height = tileHeight;
            }

            var colour = YuvColour.Of(colr, track);
            uint alignment = MediaCodecs.DecoderAlignment(codec);
            int stride = (int)MediaUtils.RoundToMultipleOf((uint)tileWidth, alignment);
            int planeHeight = (int)MediaUtils.RoundToMultipleOf((uint)tileHeight, alignment);

            var image = new Bgra(width, height);
            using var decoder = MediaCodecs.CreateVideoDecoder(codec, new VideoDecoderOptions
            {
                Width = (uint)tileWidth,
                Height = (uint)tileHeight,
                FpsNom = 1,
                FpsDenom = 1,
            });
            decoder.Initialize();
            byte[] nv12 = new byte[decoder.OutputSize];
            int decoded = 0;

            // Each tile a picture of its own, timed by its place: the decoder hands the time back with it.
            void TakeOutput()
            {
                while (decoder.ProcessOutput(ref nv12, out _, out long time))
                {
                    int index = (int)Math.Round(time / (double)TimeSpan.TicksPerSecond);
                    if (index < 0 || index >= tiles.Count)
                        continue;
                    image.PutNV12(nv12, stride, planeHeight, tileWidth, tileHeight,
                        index % columns * tileWidth, index / columns * tileHeight, colour);
                    decoded++;
                }
            }

            for (int i = 0; i < tiles.Count; i++)
            {
                long time = i * TimeSpan.TicksPerSecond;
                var units = new List<ArraySegment<byte>>();
                if (i == 0)
                {
                    foreach (var unit in track.GetContainerSamples())
                        units.Add(new ArraySegment<byte>(unit));
                }
                units.AddRange(track.ParseSample(reader.ReadItem(tiles[i])));

                foreach (var unit in units)
                {
                    if (decoder.ProcessInput(unit, time))
                        TakeOutput();
                }
            }

            decoder.BeginDrain();
            TakeOutput();
            decoder.EndDrain();

            if (decoded != tiles.Count)
                throw new InvalidDataException($"{decoded} of the {tiles.Count} tiles were decoded.");
            return image;
        }

        public void Dispose()
        {
            _picture = null;
        }

        /// <summary>How YCbCr is made into RGB: the matrix's weights of red and blue, and whether the values are of the full range.</summary>
        private readonly struct YuvColour
        {
            public readonly double Kr, Kb;
            public readonly bool FullRange;

            private YuvColour(double kr, double kb, bool fullRange)
            {
                Kr = kr;
                Kb = kb;
                FullRange = fullRange;
            }

            /// <summary>
            /// Of the item's 'nclx', or where it has none of the stream's VUI, as HEIF says; where neither says, BT.601 of the
            /// limited range, as HEVC takes a stream that says nothing to be.
            /// </summary>
            public static YuvColour Of(ColourInformationBox colr, ITrack track)
            {
                uint matrix = 2;
                bool fullRange = false;
                if (colr != null)
                {
                    matrix = colr.MatrixCoefficients;
                    fullRange = colr.FullRangeFlag;
                }
                else if (track is H265Track hevc && hevc.Sps.Values.FirstOrDefault() is SharpH265.SeqParameterSetRbsp sps
                    && sps.VuiParametersPresentFlag != 0 && sps.VuiParameters is SharpH265.VuiParameters vui)
                {
                    if (vui.ColourDescriptionPresentFlag != 0)
                        matrix = vui.MatrixCoeffs;
                    fullRange = vui.VideoFullRangeFlag != 0;
                }

                return matrix switch
                {
                    1 => new YuvColour(0.2126, 0.0722, fullRange),  // BT.709
                    9 or 10 => new YuvColour(0.2627, 0.0593, fullRange), // BT.2020
                    _ => new YuvColour(0.299, 0.114, fullRange),  // BT.601: 5 and 6, and what says nothing
                };
            }
        }

        /// <summary>A picture of BGRA, top-down, of rows of its width.</summary>
        private sealed class Bgra
        {
            public readonly int Width, Height;
            public readonly byte[] Pixels;

            public Bgra(int width, int height)
            {
                Width = width;
                Height = height;
                Pixels = new byte[width * height * 4];
            }

            /// <summary>
            /// An NV12 picture of a decoder - its planes of rows of the stride, the chroma's after the luma's plane height -
            /// put at a place, made into BGRA: what of it falls outside is cut off.
            /// </summary>
            public void PutNV12(byte[] nv12, int stride, int planeHeight, int width, int height, int left, int top, YuvColour colour)
            {
                int rows = Math.Min(height, Height - top);
                int columns = Math.Min(width, Width - left);
                if (rows <= 0 || columns <= 0)
                    return;

                double yScale = colour.FullRange ? 1 / 255.0 : 1 / 219.0, yOffset = colour.FullRange ? 0 : 16;
                double cScale = colour.FullRange ? 1 / 255.0 : 1 / 224.0;
                double kr = colour.Kr, kb = colour.Kb, kg = 1 - kr - kb;
                double crToR = 2 * (1 - kr), cbToB = 2 * (1 - kb);
                double cbToG = -2 * (1 - kb) * kb / kg, crToG = -2 * (1 - kr) * kr / kg;
                int chroma = stride * planeHeight;

                Parallel.For(0, rows, row =>
                {
                    int yRow = row * stride;
                    int cRow = chroma + row / 2 * stride;
                    int target = ((top + row) * Width + left) * 4;
                    for (int x = 0; x < columns; x++, target += 4)
                    {
                        double y = (nv12[yRow + x] - yOffset) * yScale;
                        int c = cRow + (x & ~1);
                        double cb = (nv12[c] - 128) * cScale;
                        double cr = (nv12[c + 1] - 128) * cScale;

                        Pixels[target] = ToByte(y + cbToB * cb);
                        Pixels[target + 1] = ToByte(y + cbToG * cb + crToG * cr);
                        Pixels[target + 2] = ToByte(y + crToR * cr);
                        Pixels[target + 3] = 255;
                    }
                });
            }

            private static byte ToByte(double value) => (byte)Math.Clamp((int)(value * 255 + 0.5), 0, 255);

            /// <summary>Two pictures of one height, the first on the left.</summary>
            public static Bgra SideBySide(Bgra left, Bgra right)
            {
                var both = new Bgra(left.Width + right.Width, Math.Max(left.Height, right.Height));
                for (int row = 0; row < both.Height; row++)
                {
                    if (row < left.Height)
                        Buffer.BlockCopy(left.Pixels, row * left.Width * 4, both.Pixels, row * both.Width * 4, left.Width * 4);
                    if (row < right.Height)
                        Buffer.BlockCopy(right.Pixels, row * right.Width * 4, both.Pixels, (row * both.Width + left.Width) * 4, right.Width * 4);
                }
                return both;
            }

            /// <summary>Turned anticlockwise by so many quarters, as 'irot' turns.</summary>
            public Bgra RotateAnticlockwise(int quarters)
            {
                if (quarters == 0)
                    return this;

                var turned = quarters == 2 ? new Bgra(Width, Height) : new Bgra(Height, Width);
                var source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Pixels).ToArray();
                var target = new uint[source.Length];
                int w = Width, h = Height, tw = turned.Width;
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        // where the pixel at (x, y) goes
                        var (tx, ty) = quarters switch
                        {
                            1 => (y, w - 1 - x),
                            2 => (w - 1 - x, h - 1 - y),
                            _ => (h - 1 - y, x),
                        };
                        target[ty * tw + tx] = source[y * w + x];
                    }
                });
                Buffer.BlockCopy(target, 0, turned.Pixels, 0, turned.Pixels.Length);
                return turned;
            }

            /// <summary>
            /// Mirrored as 'imir' says: about a vertical axis - left and right swapped - of axis 0, about a horizontal one - top
            /// and bottom - of 1.
            /// </summary>
            public Bgra Mirror(bool horizontalAxis)
            {
                var mirrored = new Bgra(Width, Height);
                int rowBytes = Width * 4;
                for (int y = 0; y < Height; y++)
                {
                    if (horizontalAxis)
                    {
                        Buffer.BlockCopy(Pixels, y * rowBytes, mirrored.Pixels, (Height - 1 - y) * rowBytes, rowBytes);
                        continue;
                    }
                    for (int x = 0; x < Width; x++)
                        Buffer.BlockCopy(Pixels, y * rowBytes + x * 4, mirrored.Pixels, y * rowBytes + (Width - 1 - x) * 4, 4);
                }
                return mirrored;
            }

            /// <summary>Cropped to the clean aperture of a 'clap': of its size, centred off the picture's centre by its offsets.</summary>
            public Bgra Crop(CleanApertureBox clap)
            {
                static double Fraction(uint n, uint d) => d == 0 ? 0 : (int)n / (double)d;

                int width = Math.Clamp((int)Math.Round(Fraction(clap.CleanApertureWidthN, clap.CleanApertureWidthD)), 1, Width);
                int height = Math.Clamp((int)Math.Round(Fraction(clap.CleanApertureHeightN, clap.CleanApertureHeightD)), 1, Height);
                double centreX = Fraction(clap.HorizOffN, clap.HorizOffD) + (Width - 1) / 2.0;
                double centreY = Fraction(clap.VertOffN, clap.VertOffD) + (Height - 1) / 2.0;
                int left = Math.Clamp((int)Math.Round(centreX - (width - 1) / 2.0), 0, Width - width);
                int top = Math.Clamp((int)Math.Round(centreY - (height - 1) / 2.0), 0, Height - height);
                if (left == 0 && top == 0 && width == Width && height == Height)
                    return this;

                var cropped = new Bgra(width, height);
                for (int y = 0; y < height; y++)
                    Buffer.BlockCopy(Pixels, ((top + y) * Width + left) * 4, cropped.Pixels, y * width * 4, width * 4);
                return cropped;
            }
        }
    }
}
