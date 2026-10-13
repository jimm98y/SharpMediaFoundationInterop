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
    /// decoded and put in their place, made into BGRA of sRGB as its color says, then turned, mirrored and cropped as its
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

        /// <summary>A picture: shown and held, of no time.</summary>
        public bool IsStill => true;

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

            // a spatial photo's two eyes, left then right, where the file groups them; else the primary item
            var stereo = reader.Groups.OfType<StereoEntityGroupBox>().FirstOrDefault(x => x.EntityId?.Length == 2);
            var ids = stereo != null ? new[] { stereo.EntityId[0], stereo.EntityId[1] } : new[] { reader.PrimaryItemID };

            // Each item's picture as it is shown, side by side, the first on the left: the one picture made, and each tile
            // of each item converted into its place of it - cropped, turned and flipped as it goes - not a picture of each
            // item, and of each of its transformations, copied on.
            var items = new List<Item>();
            try
            {
                foreach (var id in ids)
                    items.Add(Item.Of(reader, id));

                int width = items.Sum(x => x.ShownWidth), height = items.Max(x => x.ShownHeight);
                if ((long)width * height * 4 > Array.MaxLength)
                    throw new NotSupportedException($"An image of {width}x{height} is larger than an array holds.");
                var picture = new byte[width * height * 4];
                int left = 0;
                foreach (var item in items)
                {
                    item.Decode(reader, picture, width, left);
                    left += item.ShownWidth;
                }

                _picture = picture;
                VideoInfo = new VideoInfo
                {
                    Width = (uint)width,
                    OriginalWidth = (uint)width,
                    Height = (uint)height,
                    OriginalHeight = (uint)height,
                    FpsNom = 1,
                    FpsDenom = 1,
                    PixelFormat = PixelFormat.BGRA32,
                    StereoLayout = stereo != null ? StereoLayout.SideBySide : StereoLayout.Mono,
                };
            }
            finally
            {
                foreach (var item in items)
                    item.Dispose();
            }
        }

        private static readonly uint GridType = IsoStream.FromFourCC("grid");

        /// <summary>
        /// An item's image: coded, or a grid of coded tiles - the decoder, its frames and their color - and as its
        /// transformative properties say it is shown: of its clean aperture, then turned and flipped, as HEIF orders them.
        /// </summary>
        private sealed class Item : IDisposable
        {
            private ITrack _track;
            private VideoCodec _codec;
            private IReadOnlyList<uint> _tiles;
            private int _columns;
            private int _tileWidth, _tileHeight;
            private int _stride, _planeHeight;
            private VideoFrameFormat _format;
            private VideoColor _color;

            // the picture: of the canvas - the grid's output, or the one tile - the clean aperture's part
            private int _cropLeft, _cropTop, _width, _height;
            private VideoOrientation _orientation;

            public int ShownWidth => _orientation.SwapsAxes ? _height : _width;
            public int ShownHeight => _orientation.SwapsAxes ? _width : _height;

            public static Item Of(ImageReader reader, uint itemID)
            {
                var item = new Item();
                try
                {
                    item.Plan(reader, itemID);
                    return item;
                }
                catch
                {
                    item.Dispose();
                    throw;
                }
            }

            private void Plan(ImageReader reader, uint itemID)
            {
                var properties = reader.PropertiesOf(itemID);

                // The tiles of a grid, of its color where it says it, else of its tiles', as they are all coded alike. The
                // tiles' own transformations, which there should be none of, are not applied.
                int canvasWidth = -1, canvasHeight = -1;
                var colors = properties.OfType<ColourInformationBox>().ToList();
                if (reader.ItemTypeOf(itemID) == GridType)
                {
                    var grid = reader.ReadGrid(itemID);
                    _tiles = reader.DerivedFrom(itemID);
                    if (_tiles.Count != grid.Rows * grid.Columns)
                        throw new InvalidDataException($"The grid {itemID} of {grid.Rows}x{grid.Columns} has {_tiles.Count} tiles.");
                    _columns = grid.Columns;
                    (canvasWidth, canvasHeight) = ((int)grid.OutputWidth, (int)grid.OutputHeight);
                    foreach (var tileColor in reader.PropertiesOf(_tiles[0]).OfType<ColourInformationBox>())
                    {
                        if (!colors.Any(x => x.ColourType == tileColor.ColourType))
                            colors.Add(tileColor);
                    }
                }
                else
                {
                    _tiles = new[] { itemID };
                    _columns = 1;
                }

                _track = reader.CreateTrack(_tiles[0])
                    ?? throw new NotSupportedException($"Item {_tiles[0]} is of no codec that can be read.");

                // An AVIF's 'av1C' need have no sequence header, which is then in the item itself: read of the first tile,
                // for what it says - the size, the bits, the color - before the tiles are decoded.
                if (_track is AV1Track { SequenceHeaderObu: null } withoutHeader)
                {
                    var first = reader.ReadItem(_tiles[0]);
                    withoutHeader.ProcessSample(first, 0, first.Length, out _, out _);
                    if (withoutHeader.SequenceHeaderObu == null)
                        throw new InvalidDataException($"Item {_tiles[0]} has no AV1 sequence header.");
                }

                // the size of a tile, as its stream says it
                switch (_track)
                {
                    case H265Track hevc:
                        _codec = VideoCodec.H265;
                        var hevcSize = hevc.Sps.First().Value.CalculateDimensions();
                        (_tileWidth, _tileHeight) = ((int)hevcSize.Width, (int)hevcSize.Height);
                        break;
                    case H264Track avc:
                        _codec = VideoCodec.H264;
                        var avcSize = avc.Sps.First().Value.CalculateDimensions();
                        (_tileWidth, _tileHeight) = ((int)avcSize.Width, (int)avcSize.Height);
                        break;
                    case AV1Track av1:
                        _codec = VideoCodec.AV1;
                        var av1Size = av1.SequenceHeaderObu.CalculateDimensions();
                        (_tileWidth, _tileHeight) = ((int)av1Size.Width, (int)av1Size.Height);
                        break;
                    default:
                        throw new NotSupportedException($"Images of {_track.GetType().Name} are not supported.");
                }
                if (canvasWidth < 0)
                    (canvasWidth, canvasHeight) = (_tileWidth, _tileHeight);

                // the bits of its samples and its chroma, what the decoder hands out of them, and their color
                var (bitDepth, chromaFormat) = TrackColor.SamplesOf(_track) ?? (8, 1);
                TrackColor.ThrowIfNotDecoded(_track, bitDepth, chromaFormat);
                _format = TrackColor.FrameFormatOf(chromaFormat, bitDepth);
                _color = TrackColor.Of(colors, _track, bitDepth, sceneReferred: true);

                // the decoder's frames, of the coded size - of HEVC its SPS's, the picture cropped of them - the tile at their top left
                uint alignment = MediaCodecs.DecoderAlignment(_codec);
                var (frameWidth, frameHeight) = _track is H265Track hevcTrack
                    ? TrackColor.HevcFrameSize(hevcTrack.Sps.First().Value, (uint)_tileWidth, (uint)_tileHeight)
                    : (MediaUtils.RoundToMultipleOf((uint)_tileWidth, alignment), MediaUtils.RoundToMultipleOf((uint)_tileHeight, alignment));
                _stride = (int)VideoFrameFormats.Stride(_format, frameWidth);
                _planeHeight = (int)frameHeight;

                // of the canvas, the clean aperture, then turned and flipped: each 'irot' and 'imir' after the one before
                (_cropLeft, _cropTop, _width, _height) = (0, 0, canvasWidth, canvasHeight);
                foreach (var property in properties)
                {
                    switch (property)
                    {
                        case CleanApertureBox clap:
                            (_cropLeft, _cropTop, _width, _height) = CleanAperture(clap, canvasWidth, canvasHeight);
                            break;
                        case ImageRotation irot:
                            // anticlockwise, of quarters
                            _orientation = _orientation.Then(VideoOrientation.Of((4 - (irot.Angle & 3)) % 4 * 90));
                            break;
                        case ImageMirror imir:
                            // about a vertical axis - left and right swapped - of axis 0, about a horizontal one - top and bottom - of 1
                            _orientation = _orientation.Then(imir.Axis ? new VideoOrientation(1, 0, 0, -1) : new VideoOrientation(-1, 0, 0, 1));
                            break;
                    }
                }
            }

            /// <summary>
            /// The clean aperture of a 'clap', of a picture: of its size, centred off the picture's centre by its offsets -
            /// where it starts, and its size.
            /// </summary>
            private static (int Left, int Top, int Width, int Height) CleanAperture(CleanApertureBox clap, int pictureWidth, int pictureHeight)
            {
                static double Fraction(uint n, uint d) => d == 0 ? 0 : (int)n / (double)d;

                int width = Math.Clamp((int)Math.Round(Fraction(clap.CleanApertureWidthN, clap.CleanApertureWidthD)), 1, pictureWidth);
                int height = Math.Clamp((int)Math.Round(Fraction(clap.CleanApertureHeightN, clap.CleanApertureHeightD)), 1, pictureHeight);
                double centreX = Fraction(clap.HorizOffN, clap.HorizOffD) + (pictureWidth - 1) / 2.0;
                double centreY = Fraction(clap.VertOffN, clap.VertOffD) + (pictureHeight - 1) / 2.0;
                int left = Math.Clamp((int)Math.Round(centreX - (width - 1) / 2.0), 0, pictureWidth - width);
                int top = Math.Clamp((int)Math.Round(centreY - (height - 1) / 2.0), 0, pictureHeight - height);
                return (left, top, width, height);
            }

            /// <summary>
            /// The tiles decoded, each converted into its place of the picture as it is shown, at a column of the target - a
            /// picture of rows of so many pixels, BGRA.
            /// </summary>
            public void Decode(ImageReader reader, byte[] target, int targetWidth, int targetX)
            {
                using var decoder = MediaCodecs.CreateVideoDecoder(_codec, new VideoDecoderOptions
                {
                    Width = (uint)_tileWidth,
                    Height = (uint)_tileHeight,
                    FpsNom = 1,
                    FpsDenom = 1,
                    OutputFormat = _format,
                });
                decoder.Initialize();

                // the decoder's frame: a tile's, or of a large image's one item, as large as its picture
                byte[] frame = new byte[decoder.OutputSize];
                int decoded = 0;

                // Each tile a picture of its own, timed by its place: the decoder hands the time back with it.
                void TakeOutput()
                {
                    while (decoder.ProcessOutput(ref frame, out _, out long time))
                    {
                        int index = (int)Math.Round(time / (double)TimeSpan.TicksPerSecond);
                        if (index < 0 || index >= _tiles.Count)
                            continue;
                        var placement = new FramePlacement(index % _columns * _tileWidth - _cropLeft, index / _columns * _tileHeight - _cropTop,
                            _width, _height, _orientation, targetX, 0);
                        _color.Convert(frame, _format, _stride, _planeHeight, _tileWidth, _tileHeight, target, targetWidth, 4, placement, exact: true);
                        decoded++;
                    }
                }

                var units = new List<ArraySegment<byte>>();
                for (int i = 0; i < _tiles.Count; i++)
                {
                    long time = i * TimeSpan.TicksPerSecond;
                    units.Clear();
                    if (i == 0)
                    {
                        foreach (var unit in _track.GetContainerSamples())
                            units.Add(new ArraySegment<byte>(unit));
                    }
                    units.AddRange(_track.ParseSample(reader.ReadItem(_tiles[i])));

                    foreach (var unit in units)
                    {
                        if (decoder.ProcessInput(unit, time))
                            TakeOutput();
                    }
                }

                decoder.BeginDrain();
                TakeOutput();
                decoder.EndDrain();

                if (decoded != _tiles.Count)
                    throw new InvalidDataException($"{decoded} of the {_tiles.Count} tiles were decoded.");
            }

            public void Dispose()
            {
                _track?.Dispose();
                _track = null;
            }
        }

        public void Dispose()
        {
            _picture = null;
        }
    }
}
