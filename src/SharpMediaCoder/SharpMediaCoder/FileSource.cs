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

        public FileSource(string path)
        {
            this._path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public async override Task InitializeAsync()
        {
            Info = await LoadFileAsync(_path);
        }

        public async override Task FinalizeAsync()
        {
            // restart playback
            await InitializeAsync();
        }

        private async Task<VideoInfo> LoadFileAsync(string fileName)
        {
            _videoSampleQueue.Clear();

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
                }
            }

            return videoInfo;
        }
    }
}
