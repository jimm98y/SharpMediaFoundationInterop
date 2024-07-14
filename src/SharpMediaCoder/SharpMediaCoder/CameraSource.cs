using SharpMediaFoundation.Input;

namespace SharpMediaFoundation.WPF
{
    public class CameraSource : VideoSourceBase
    {
        private MFSource _device;

        public CameraSource()
        {
        }

        public async override Task InitializeAsync()
        {
            Info = await OpenAsync();
        }

        private async Task<VideoInfo> OpenAsync()
        {
            _device = new MFSource();
            

            var videoInfo = new VideoInfo();
            return videoInfo;
        }
    }
}
