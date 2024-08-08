using SharpMediaFoundation.Transforms.H264;
using SharpMediaFoundation.Transforms.H265;
using SharpMediaFoundation.Utils;
using SharpMp4;
using System.IO;

namespace SharpMediaFoundation.WPF
{
    public class FileSource : VideoSourceBase
    {
        private string _path;

        private SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1);

        public FileSource(string path)
        {
            this._path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public async override Task InitializeAsync()
        {
            await _semaphoreSlim.WaitAsync();
            try
            {
                if (VideoInfo == null)
                {
                    var ret = await LoadFileAsync(_path);
                    VideoInfo = ret.Video;
                    AudioInfo = ret.Audio;
                }
            }
            finally
            {
                _semaphoreSlim.Release();
            }
        }

        private async Task<(VideoInfo Video, AudioInfo Audio)> LoadFileAsync(string fileName)
        {
            _videoSampleQueue.Clear();
            _audioSampleQueue.Clear();

            VideoInfo videoInfo = new VideoInfo();
            AudioInfo audioInfo = null;
            using (Stream fs = new BufferedStream(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                using (var fmp4 = await FragmentedMp4.ParseAsync(fs))
                {
                    var videoTrackBox = fmp4.FindVideoTracks().FirstOrDefault();
                    var audioTrackBox = fmp4.FindAudioTracks().FirstOrDefault();
                    var parsedMDAT = await fmp4.ParseMdatAsync();

                    var videoTrackId = fmp4.FindVideoTrackID().First();
                    var audioTrackId = fmp4.FindAudioTrackID().FirstOrDefault();

                    var vsbox = 
                        videoTrackBox
                            .GetMdia()
                            .GetMinf()
                            .GetStbl()
                            .GetStsd()
                            .Children.Single((Mp4Box x) => x is VisualSampleEntryBox) as VisualSampleEntryBox;
                    videoInfo.OriginalWidth = vsbox.Width;
                    videoInfo.OriginalHeight = vsbox.Height;
                    videoInfo.FpsNom = fmp4.CalculateTimescale(videoTrackBox);
                    videoInfo.FpsDenom = fmp4.CalculateSampleDuration(videoTrackBox);
                    if (vsbox.Children.FirstOrDefault(x => x is AvcConfigurationBox) != null)
                    {
                        videoInfo.VideoCodec = "H264";
                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H264Decoder.H264_RES_MULTIPLE);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H264Decoder.H264_RES_MULTIPLE);
                    }
                    else if (vsbox.Children.FirstOrDefault(x => x is HevcConfigurationBox) != null)
                    {
                        videoInfo.VideoCodec = "H265";
                        videoInfo.Width = MediaUtils.RoundToMultipleOf(videoInfo.OriginalWidth, H265Decoder.H265_RES_MULTIPLE);
                        videoInfo.Height = MediaUtils.RoundToMultipleOf(videoInfo.OriginalHeight, H265Decoder.H265_RES_MULTIPLE);
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }

                    foreach (var au in parsedMDAT[videoTrackId])
                    {
                        _videoSampleQueue.Enqueue(au);
                    }

                    if (audioTrackBox != null)
                    {
                        audioInfo = new AudioInfo();

                        var sourceAudioSampleBox =
                            audioTrackBox
                                .GetMdia()
                                .GetMinf()
                                .GetStbl()
                                .GetStsd()
                                .Children.Single((Mp4Box x) => x is AudioSampleEntryBox) as AudioSampleEntryBox;
                        var audioDescriptor = sourceAudioSampleBox.GetAudioSpecificConfigDescriptor();

                        audioInfo.AudioCodec = "AAC"; // TODO
                        audioInfo.BitsPerSample = 16;
                        audioInfo.UserData = await audioDescriptor.ToBytes();
                        audioInfo.Channels = (uint)audioDescriptor.ChannelConfiguration;
                        audioInfo.SampleRate = (uint)audioDescriptor.GetSamplingFrequency();

                        foreach (var frame in parsedMDAT[audioTrackId][0])
                        {
                            _audioSampleQueue.Enqueue(frame);
                        }
                    }
                }
            }

            return (videoInfo, audioInfo);
        }
    }
}
