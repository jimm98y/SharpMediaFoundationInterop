using System;
using SharpMediaFoundationInterop.Utils;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SharpMediaFoundationInterop.Transforms
{
    public abstract class VideoTransformBase : MediaTransformBase, IMediaVideoTransform
    {
        protected long _sampleDuration = 1;
        protected IMFTransform _transform;
        private MFT_OUTPUT_DATA_BUFFER[] _dataBuffer;
        private bool _disposedValue;

        public uint OriginalWidth { get; }
        public uint OriginalHeight { get; }

        public uint Width { get; }
        public uint Height { get; }

        /// <summary>The frame rate, where the stream says it; 0 over 0 where it does not.</summary>
        public uint FpsNom { get; }
        public uint FpsDenom { get; }

        /// <summary>
        /// Whether the frame rate is known. A decoder is told it only then, as a hint: it times nothing by it, and a rate
        /// made up for a stream that has none - an RTP stream, which times every frame of its own - is no better than none.
        /// </summary>
        public bool HasFrameRate => FpsNom > 0 && FpsDenom > 0;

        public uint OutputSize { get; private set; }

        protected VideoTransformBase(uint width, uint height)
          : this(1, width, height, 1, 1)
        { }

        protected VideoTransformBase(uint resMultiple, uint width, uint height, uint fpsNom, uint fpsDenom)
        {
            FpsNom = fpsNom;
            FpsDenom = fpsDenom;
            _sampleDuration = HasFrameRate ? MediaUtils.CalculateSampleDuration(FpsNom, FpsDenom) : 0;

            OriginalWidth = width;
            OriginalHeight = height;
            Width = MediaUtils.RoundToMultipleOf(width, resMultiple);
            Height = MediaUtils.RoundToMultipleOf(height, resMultiple);
        }

        public void Initialize()
        {
            _transform = Create();
            _transform.GetOutputStreamInfo(0, out var streamInfo);
            _dataBuffer = MediaUtils.CreateOutputDataBuffer(streamInfo.cbSize);
            OutputSize = streamInfo.cbSize;
        }

        protected abstract IMFTransform Create();

        public bool ProcessInput(byte[] data, long timestamp)
        {
            return ProcessInput(new ReadOnlySpan<byte>(data), timestamp);
        }

        /// <summary>
        /// One sample in - an access unit, a NAL unit, a frame - copied straight into the transform's media buffer: a
        /// view of a reader's buffer or of a pooled one goes in without a managed copy.
        /// </summary>
        public virtual bool ProcessInput(ReadOnlySpan<byte> data, long timestamp)
        {
            return ProcessInput(_transform, ReadOnlySpan<byte>.Empty, data, _sampleDuration, timestamp);
        }

        /// <summary>One sample in, of <paramref name="prefix"/> - a start code - and then <paramref name="data"/>.</summary>
        protected bool ProcessInput(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data, long timestamp)
        {
            return ProcessInput(_transform, prefix, data, _sampleDuration, timestamp);
        }

        public bool ProcessOutput(ref byte[] buffer, out uint length)
        {
            return ProcessOutput(_transform, _dataBuffer, ref buffer, out length);
        }

        /// <summary>
        /// Same as <see cref="ProcessOutput(ref byte[], out uint)"/>, but also reports the decoded
        /// frame's presentation time. Decoders reorder pictures, so this is what identifies which
        /// input a frame came from.
        /// </summary>
        public bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            return ProcessOutput(_transform, _dataBuffer, ref buffer, out length, out timestamp);
        }

        public virtual bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        /// <summary>
        /// Asks the transform to emit everything it still holds. Call <see cref="ProcessOutput(ref byte[], out uint)"/>
        /// until it returns false before calling <see cref="EndDrain"/>, otherwise restarting the
        /// stream discards the frames the drain just queued.
        /// </summary>
        public virtual void BeginDrain()
        {
            // End of stream then drain, and nothing else: notifying end of streaming first tells
            // the transform to release its resources, which costs the frames still queued.
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, default);
        }

        /// <summary>Restarts the transform so it accepts input again after a drain.</summary>
        public virtual void EndDrain()
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, default);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, default);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    if (_transform != null)
                    {
                        DestroyTransform(_transform);
                        _transform = null;
                    }
                }

                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
