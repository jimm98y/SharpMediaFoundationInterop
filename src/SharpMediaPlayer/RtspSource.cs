using SharpAV1;
using SharpAVX;
using SharpVP9;
using SharpH264;
using SharpH265;
using SharpH26X;
using SharpISOBMFF;
using SharpISOBMFF.Extensions;
using SharpMediaFoundationInterop.Codecs;
using SharpMediaFoundationInterop.Utils;
using SharpRTSPClient;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;

namespace SharpMediaFoundationInterop.WPF
{
    public class RtspSource : RtpSourceBase
    {
        private RtspClient _rtspClient;
        private string _uri;
        private string _userName;
        private string _password;

        public RtspSource(string uri, string userName = null, string password = null)
        {
            this._uri = uri ?? throw new ArgumentNullException(nameof(uri));
            this._userName = userName;
            this._password = password;
        }

        public async override Task InitializeAsync()
        {
            // One connection, made once: the client reconnects by itself, and initialized again - by the sound's thread as
            // by the video's - a second would be made beside it, feeding the same queues and decoders.
            if (_rtspClient == null)
            {
                var ret = await CreateClient(_uri, _userName, _password);
                VideoInfo = ret.Video;
                AudioInfo = ret.Audio;
            }
        }

        private async Task<(VideoInfo Video, AudioInfo Audio)> CreateClient(string uri, string userName, string password)
        {
            // Completed from the client's events, on its thread that reads the connection: run on that thread, whatever
            // awaits the setup - a caller with no synchronization context - kept it from reading another packet.
            var tcsSetupCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            VideoInfo videoInfo = null;
            AudioInfo audioInfo = null;

            // The first video track and the first audio track, which the client sets up when AcceptTrack is left unset;
            // their data is told apart by the index of its track.
            int videoTrackIndex = -1, audioTrackIndex = -1;

            _rtspClient = new RtspClient();
            _rtspClient.NewTrack += (o, e) =>
            {
                if (e.Kind == TrackKind.Video && videoTrackIndex < 0)
                {
                    videoTrackIndex = e.TrackIndex;
                    videoInfo = CreateVideoInfo(e);
                }
                else if (e.Kind == TrackKind.Audio && audioTrackIndex < 0)
                {
                    audioTrackIndex = e.TrackIndex;
                    audioInfo = CreateAudioInfo(e);
                }
            };
            _rtspClient.SetupMessageCompleted += (o, e) =>
            {
                if (videoInfo != null && videoInfo.Width > 0 && videoInfo.Height > 0)
                {
                    tcsSetupCompleted.TrySetResult(true);
                }
            };

            _rtspClient.ReceivedData += (o, e) =>
            {
                if (e.TrackIndex == videoTrackIndex)
                {
                    var sample = RentUnits();
                    var parameterSets = Interlocked.Exchange(ref _parameterSets, null);
                    if (parameterSets != null)
                    {
                        foreach (var parameterSet in parameterSets)
                            sample.Add(parameterSet);
                    }
                    foreach (var received in e.Data.Data)
                        sample.Add(received.Span);
                    sample.Timestamp = VideoTime(e.Data.RtpTimestamp, e.Data.HasSenderSync, e.Data.Timestamp);

                    // AV1 and VP9 say nothing of the picture's size in the SDP: the first sequence header, or key frame, does
                    if (videoInfo.Width == 0 || videoInfo.Height == 0)
                    {
                        if (TryReadSize(videoInfo.VideoCodec, sample.Units, out VideoInfo sized))
                        {
                            videoInfo.OriginalWidth = sized.OriginalWidth;
                            videoInfo.OriginalHeight = sized.OriginalHeight;
                            videoInfo.Width = sized.Width;
                            videoInfo.Height = sized.Height;
                            tcsSetupCompleted.TrySetResult(true);
                        }
                    }
                    _videoSampleQueue.Enqueue(sample);
                }
                else if (e.TrackIndex == audioTrackIndex)
                {
                    // the time of the packet's first frame: that of the rest follows from the frames decoded before them
                    long time = AudioTime(e.Data.HasSenderSync, e.Data.Timestamp);
                    foreach (var received in e.Data.Data)
                    {
                        var frame = RentUnits();
                        frame.Add(received.Span);
                        frame.Timestamp = time;
                        time = -1;
                        _audioSampleQueue.Enqueue(frame);
                    }
                }
            };
            _rtspClient.Connect(uri, RtpTransport.TCP, userName, password);

            await tcsSetupCompleted.Task;

            return (videoInfo, audioInfo);
        }

        private VideoInfo CreateVideoInfo(NewTrackEventArgs e)
        {
            var videoInfo = new VideoInfo();

            if (e.StreamConfigurationData is H264StreamConfigurationData h264cfg)
            {
                _parameterSets = _streamParameterSets = new[] { h264cfg.SPS, h264cfg.PPS };
                videoInfo = CreateH264VideoInfo(h264cfg.SPS);
            }
            else if (e.StreamConfigurationData is H265StreamConfigurationData h265cfg)
            {
                _parameterSets = _streamParameterSets = new[] { h265cfg.VPS, h265cfg.SPS, h265cfg.PPS };
                videoInfo = CreateH265VideoInfo(h265cfg.SPS);
            }
            else if (e.StreamConfigurationData is H266StreamConfigurationData)
            {
                // H266 is as of 8/3/2025 not supported by Media Foundation
                throw new NotSupportedException();
            }
            else if (e.Codec == "AV1" || e.Codec == "VP9")
            {
                // the size comes with the first sequence header, or key frame - see TryReadSize
                videoInfo.VideoCodec = e.Codec;
            }
            else
            {
                throw new NotSupportedException($"Video codec {e.Codec}");
            }

            // A frame rate is left unknown where the stream does not say it: every frame is shown by its RTP timestamp.

            return videoInfo;
        }

        private static AudioInfo CreateAudioInfo(NewTrackEventArgs e)
        {
            var audioInfo = new AudioInfo();

            if (e.StreamConfigurationData is AACStreamConfigurationData aaccfg)
            {
                audioInfo.AudioCodec = "AAC";
                audioInfo.BitsPerSample = 16;

                var descriptor = new AudioSpecificConfig();
                descriptor.SamplingFrequencyIndex = (byte)aaccfg.FrequencyIndex;
                descriptor.ChannelConfiguration = (byte)aaccfg.ChannelConfiguration;
                descriptor.ExtensionAudioObjectType = new GetAudioObjectType() { AudioObjectTypeExt = 5 }; // TODO
                descriptor.AudioObjectType = new GetAudioObjectType() { AudioObjectType = 2 }; // TODO simplify API
                descriptor._GASpecificConfig = new GASpecificConfig(aaccfg.FrequencyIndex, aaccfg.ChannelConfiguration, 2);

                audioInfo.UserData = descriptor.ToBytes();
                audioInfo.ChannelCount = aaccfg.ChannelConfiguration == 1 ? 1u : (uint)aaccfg.ChannelConfiguration; // ChannelConfiguration = 1 means mono even though ChannelCount = 2
                audioInfo.ChannelConfiguration = aaccfg.ChannelConfiguration;
                audioInfo.SampleRate = AudioSpecificConfigDescriptor.SamplingFrequencyMap[(uint)aaccfg.FrequencyIndex];
            }
            else if (e.Codec == "OPUS")
            {
                audioInfo.AudioCodec = "OPUS";
                audioInfo.BitsPerSample = 32;
                audioInfo.ChannelCount = 2;
                audioInfo.SampleRate = 48000;
            }
            else
            {
                throw new NotSupportedException($"Audio codec {e.Codec}");
            }

            return audioInfo;
        }
    }
}
