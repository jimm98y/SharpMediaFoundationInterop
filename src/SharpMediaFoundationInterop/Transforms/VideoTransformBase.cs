using System;
using System.Runtime.InteropServices;
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

        /// <summary>
        /// The device to decode on, the GPU's: given before <see cref="Initialize"/>, to a transform that can use one. Its
        /// output is then of the GPU's memory, handed out by <see cref="ProcessOutput(out IMFSample, out long)"/>.
        /// </summary>
        public IMFDXGIDeviceManager DeviceManager { get; set; }

        /// <summary>Whether the transform took the <see cref="DeviceManager"/>: some cannot use one.</summary>
        public bool UsesDevice { get; private set; }

        /// <summary>
        /// Whether the transform hands out samples of its own - of the GPU's memory, as a decoder on a device does - rather
        /// than filling one of the caller's.
        /// </summary>
        public bool ProvidesSamples { get; private set; }

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
            ProvidesSamples = (streamInfo.dwFlags & (uint)_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;
            // no sample of ours where the transform hands out its own
            _dataBuffer = MediaUtils.CreateOutputDataBuffer(ProvidesSamples ? 0 : streamInfo.cbSize);
            OutputSize = streamInfo.cbSize;
        }

        /// <summary>
        /// Gives the transform the <see cref="DeviceManager"/>, where there is one and the transform says it can use one: it
        /// decodes on the GPU from then on, where the GPU can decode its format, and on the CPU into the GPU's memory where it
        /// cannot. Called by a decoder as it is made, before its media types are set.
        /// </summary>
        protected void AttachDeviceManager(IMFTransform transform)
        {
            UsesDevice = false;
            if (DeviceManager == null)
                return;

            transform.GetAttributes(out IMFAttributes attributes);
            uint aware = 0;
            try
            {
                attributes.GetUINT32(PInvoke.MF_SA_D3D11_AWARE, out aware);
            }
            catch (Exception)
            {
                // not said: not aware
            }
            if (aware == 0)
                return;

            nint manager = Marshal.GetIUnknownForObject(DeviceManager);
            try
            {
                var result = transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)manager);
                UsesDevice = result.Succeeded;
                if (!UsesDevice && Log.WarnEnabled)
                    Log.Warn($"The transform took no device: 0x{result.Value:X8}");
            }
            finally
            {
                Marshal.Release(manager);
            }
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
        /// input a frame came from. Overridden by a decoder that hands out only some of the frames.
        /// </summary>
        public virtual bool ProcessOutput(ref byte[] buffer, out uint length, out long timestamp)
        {
            return ProcessOutput(_transform, _dataBuffer, ref buffer, out length, out timestamp);
        }

        /// <summary>
        /// The next frame as the transform hands it out, not copied - of the GPU's memory, decoding on a device - with its
        /// time. The caller's to let go of. Of a transform that <see cref="ProvidesSamples"/>. Overridden by a decoder that
        /// hands out only some of the frames.
        /// </summary>
        public virtual bool ProcessOutput(out IMFSample sample, out long timestamp)
        {
            return ProcessOutputSample(_transform, _dataBuffer, out sample, out timestamp);
        }

        public virtual bool Drain()
        {
            BeginDrain();
            EndDrain();
            return true;
        }

        public virtual void Flush()
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, default);
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
