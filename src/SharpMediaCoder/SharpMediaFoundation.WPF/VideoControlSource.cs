using SharpMediaFoundation.H264;
using SharpMediaFoundation.H265;
using SharpMediaFoundation.NV12;
using SharpMp4;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SharpMediaFoundation.WPF
{
    public class VideoControlSource
    {
        public VideoInfo Info { get; set; }

        private enum VideoCodecType
        {
            H264,
            H265
        }

        private VideoCodecType _codec;
        private IVideoTransform _videoDecoder;
        private NV12toRGB _nv12Decoder;
        private Queue<IList<byte[]>> _sampleQueue = new Queue<IList<byte[]>>();
        private Queue<byte[]> _renderQueue = new Queue<byte[]>();
        private byte[] _nv12Buffer;
        private long _time = 0;

        private string _path;
        public VideoControlSource(string path)
        {
            this._path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public async Task InitializeAsync()
        {
            Info = await LoadFileAsync(_path);
        }

        public async Task<byte[]> GetSampleAsync()
        {
            if (_videoDecoder == null || _nv12Decoder == null)
            {
                CreateDecoder(_codec, Info);
            }

            byte[] existing;
            if (_renderQueue.TryDequeue(out existing))
                return existing;

            while (_renderQueue.Count == 0 && _sampleQueue.TryDequeue(out var au))
            {
                foreach (var nalu in au)
                {
                    if (_videoDecoder.ProcessInput(nalu, _time))
                    {
                        while (_videoDecoder.ProcessOutput(ref _nv12Buffer, out _))
                        {
                            _nv12Decoder.ProcessInput(_nv12Buffer, _time);

                            byte[] decoded = ArrayPool<byte>.Shared.Rent((int)(Info.Width * Info.Height * 3));
                            _nv12Decoder.ProcessOutput(ref decoded, out _);

                            _renderQueue.Enqueue(decoded);
                        }
                    }
                }
                _time += 10000 * 1000 / (Info.FpsNom / Info.FpsDenom); // 100ns units
            }

            if (_renderQueue.TryDequeue(out existing))
            {
                return existing;
            }
            else
            {
                await InitializeAsync();
                return null;
            }
        }

        private void CreateDecoder(VideoCodecType codec, VideoInfo info)
        {
            // decoders must be created on the same thread as the samples
            if (codec == VideoCodecType.H264)
            {
                _videoDecoder = new H264Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom);
            }
            else if (codec == VideoCodecType.H265)
            {
                _videoDecoder = new H265Decoder(info.OriginalWidth, info.OriginalHeight, info.FpsNom, info.FpsDenom);
            }
            else
            {
                throw new NotSupportedException();
            }

            _nv12Decoder = new NV12toRGB(info.Width, info.Height);
            _nv12Buffer = new byte[info.Width * info.Height * 3];
        }

        private async Task<VideoInfo> LoadFileAsync(string fileName)
        {
            _sampleQueue.Clear();

            var videoInfo = new VideoInfo();
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
                    videoInfo.OriginalWidth = vsbox.Width;
                    videoInfo.OriginalHeight = vsbox.Height;
                    videoInfo.FpsNom = fmp4.CalculateTimescale(videoTrackBox);
                    videoInfo.FpsDenom = fmp4.CalculateSampleDuration(videoTrackBox);
                    if (vsbox.Children.FirstOrDefault(x => x is AvcConfigurationBox) != null)
                    {
                        _codec = VideoCodecType.H264;
                        videoInfo.Width = MathUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                        videoInfo.Height = MathUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);
                    }
                    else if (vsbox.Children.FirstOrDefault(x => x is HevcConfigurationBox) != null)
                    {
                        _codec = VideoCodecType.H265;
                        videoInfo.Width = MathUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                        videoInfo.Height = MathUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);
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
            return videoInfo;
        }
    }
}
