using Rtsp.Rtp;
using SharpMediaFoundationInterop.Codecs;
using SharpRTCP;
using SharpRTSPClient;
using SharpWebRTC;
using SharpWebRTC.SDP;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SharpMediaFoundationInterop.WPF
{
    /// <summary>
    /// A camera of an RTSPtoWebRTC gateway (https://github.com/jimm98y/SharpRTSPtoWebRTC), received over WebRTC: the gateway
    /// offers the camera's video - H.264, H.265, AV1 or VP9 - and its sound, Opus or G.711, as its RTSP source sends them; this
    /// answers, and depacketizes what comes as RTSP's source does. The signalling is the gateway's: the offer of "getoffer",
    /// the answer to "setanswer", the candidates to "addicecandidate".
    /// </summary>
    /// <remarks>
    /// A session that ends once playing - the connection failed, closed or disconnected, or no media for <see cref="MediaTimeout"/>
    /// - is connected again, every <see cref="RetryDelay"/> until it is: the frames of the new one are timed on from the last of
    /// the old, and taken from its first key frame on. Its video is of the codec and size the first was, as the decoder was made.
    /// </remarks>
    public class WebRtcSource : RtpSourceBase
    {
        private static readonly HttpClient Http = new HttpClient();

        /// <summary>How long after a failed connection - the gateway not there, the camera not known - another is tried.</summary>
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

        /// <summary>How long of no RTP a session is taken to be gone: a camera sends several frames a second.</summary>
        private static readonly TimeSpan MediaTimeout = TimeSpan.FromSeconds(5);

        /// <summary>How long a session connected again may take to its first key frame before another is tried.</summary>
        private static readonly TimeSpan KeyFrameTimeout = TimeSpan.FromSeconds(20);

        private readonly string _server;
        private readonly string _name;
        private volatile PeerConnection _connection;
        private volatile bool _disposed;

        private MediaTrack _videoTrack;
        private MediaTrack _audioTrack;
        private IPayloadProcessor _videoProcessor;
        private IPayloadProcessor _audioProcessor;
        private string _videoCodec;
        private VideoInfo _videoInfo;
        private bool _keyFrameRequested;
        private int _unsized;
        private TaskCompletionSource<bool> _videoSized;

        // of a session connected again: its frames are not taken until its first that says the picture's size
        private volatile bool _resynchronizing;
        private long _lastRtpAt = Environment.TickCount64;
        private Timer _watchdog;
        private int _reconnecting;

        // the stream's parameter sets as they last came in it, of each type: sent again after the decoder is drained
        private readonly SortedDictionary<int, byte[]> _inBandParameterSets = new SortedDictionary<int, byte[]>();

        // of each source, its clock against the sender's, of its sender reports
        private readonly ConcurrentDictionary<uint, ChannelState> _senderClocks = new ConcurrentDictionary<uint, ChannelState>();

        /// <summary>What its connection does, a line at a time - ICE, DTLS, reconnections - of anything given, before it is initialized.</summary>
        public Action<string> Trace { get; set; }

        /// <param name="server">
        /// The gateway's WebRTC API: "http://localhost:5218/api/webrtc"; of a gateway of HTTP Basic authentication, of the user
        /// and password in it - "https://user:password@host/api/webrtc" - sent of each request, not in its URL.
        /// </param>
        /// <param name="name">The camera's name, as the gateway's "getcameras" lists it.</param>
        public WebRtcSource(string server, string name)
        {
            if (server == null)
                throw new ArgumentNullException(nameof(server));
            var uri = new Uri(server);
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                // RFC 7617: base64 of user ":" password, of UTF-8; the URL's of its escapes undone
                _authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri.UserInfo))));
                server = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
            }
            _server = server.TrimEnd('/');
            _name = name ?? throw new ArgumentNullException(nameof(name));
        }

        // of a URL of a user and password, the Authorization header of each request
        private readonly AuthenticationHeaderValue _authorization;

        #region Connecting

        private readonly object _initSync = new object();
        private Task _initialization;
        private DateTime _failedAt;

        /// <summary>
        /// Connects, once: the sound's thread and the video's both initialize it, and share the one connection. A control tries
        /// again as soon as it fails - of a gateway not there, it is tried again no sooner than <see cref="RetryDelay"/> after.
        /// </summary>
        public override Task InitializeAsync()
        {
            lock (_initSync)
            {
                if (_initialization != null && _initialization.IsFaulted && DateTime.UtcNow - _failedAt >= RetryDelay)
                    _initialization = null;
                _initialization ??= InitializeOnceAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _failedAt = DateTime.UtcNow;
                    return t;
                }, TaskScheduler.Default).Unwrap();
                return _initialization;
            }
        }

        private async Task InitializeOnceAsync()
        {
            try
            {
                await ConnectAsync(Timeout.InfiniteTimeSpan);
            }
            catch
            {
                // what was made of it let go of, rather than left open until it is tried again
                CloseConnection();
                throw;
            }
            VideoInfo = _videoInfo;
            _watchdog ??= new Timer(_ => CheckMedia(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// A session: the gateway's offer, the answer to it, and - of video - its first frame that says the picture's size, of
        /// a key frame. What was of a session before is let go of.
        /// </summary>
        private async Task ConnectAsync(TimeSpan keyFrameTimeout)
        {
            CloseConnection();
            _videoSized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _videoProcessor = _audioProcessor = null;
            _keyFrameRequested = false;
            _unsized = 0;
            _inBandParameterSets.Clear();
            _senderClocks.Clear();

            string id = ((uint)Random.Shared.Next()).ToString();
            string offer;
            // the session's secret, of its offer: what its answer and candidates are taken of
            string token;
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"{_server}/getoffer?id={id}&name={Uri.EscapeDataString(_name)}") { Headers = { Authorization = _authorization } })
            using (var response = await Http.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                offer = json.RootElement.GetProperty("sdp").GetString();
                token = json.RootElement.TryGetProperty("token", out var tokenProperty) ? tokenProperty.GetString() : null;
            }

            var connection = new PeerConnection(new PeerConnectionOptions
            {
                Trace = Trace,
                // of the formats the decoders here take
                AcceptFormat = (kind, format) => kind == "video"
                    ? format.Name is "H264" or "H265" or "AV1" or "VP9"
                    : format.Name.Equals("opus", StringComparison.OrdinalIgnoreCase) || format.Name is "PCMU" or "PCMA",
            });
            var sized = _videoSized;
            connection.StateChanged += (s, state) => OnStateChanged(connection, sized, state);
            connection.RtpPacketReceived += OnRtp;
            connection.RtcpPacketReceived += OnRtcp;
            connection.LocalCandidate += (s, candidate) => _ = PostAsync($"{_server}/addicecandidate?id={id}", new { candidate = candidate.ToString(), sdpMid = "0", sdpMLineIndex = 0 }, token);
            _connection = connection;
            if (_disposed)
            {
                CloseConnection();
                throw new ObjectDisposedException(nameof(WebRtcSource));
            }

            string answer = await connection.CreateAnswerAsync(offer);
            _videoTrack = connection.Tracks.FirstOrDefault(t => t.Kind == "video");
            _audioTrack = connection.Tracks.FirstOrDefault(t => t.Kind == "audio");
            if (AudioInfo == null)
                AudioInfo = CreateAudioInfo(_audioTrack);
            if (_videoTrack == null)
                _videoSized.TrySetResult(true);
            Interlocked.Exchange(ref _lastRtpAt, Environment.TickCount64);

            await PostAsync($"{_server}/setanswer?id={id}", new { type = "answer", sdp = answer }, token);

            // the candidates the answer has, as a browser trickles them too
            foreach (var candidate in connection.IceAgent.LocalCandidates)
                await PostAsync($"{_server}/addicecandidate?id={id}", new { candidate = candidate.ToString(), sdpMid = _videoTrack?.Mid ?? _audioTrack?.Mid ?? "0", sdpMLineIndex = 0 }, token);

            // the picture's size comes with the first parameter sets, or key frame
            if (await Task.WhenAny(_videoSized.Task, Task.Delay(keyFrameTimeout)) != _videoSized.Task)
                throw new TimeoutException($"No key frame in {keyFrameTimeout.TotalSeconds} s.");
            await _videoSized.Task;
        }

        private async Task PostAsync(string url, object body, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                Headers = { Authorization = _authorization },
            };
            if (token != null)
                request.Headers.Add("X-Session-Token", token);
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        private void CloseConnection()
        {
            var connection = _connection;
            _connection = null;
            connection?.Close();
        }

        private void OnStateChanged(PeerConnection connection, TaskCompletionSource<bool> sized, PeerConnectionState state)
        {
            if (connection != _connection || _disposed)
                return; // of a session let go of
            bool gone = state == PeerConnectionState.Failed || state == PeerConnectionState.Closed || state == PeerConnectionState.Disconnected;
            if (!gone)
                return;

            // before the picture's size came - ICE of no pair that works, DTLS of no handshake - its connecting fails, rather
            // than wait; once playing, it is connected again
            if (sized.TrySetException(new IOException($"The WebRTC connection to {_server} is {state.ToString().ToLowerInvariant()}.")))
                return;
            Reconnect($"the connection is {state.ToString().ToLowerInvariant()}");
        }

        // once a second, of a session playing: whether its media still comes
        private void CheckMedia()
        {
            if (_disposed || _connection == null || Volatile.Read(ref _reconnecting) != 0)
                return;
            long silent = Environment.TickCount64 - Interlocked.Read(ref _lastRtpAt);
            if (silent > MediaTimeout.TotalMilliseconds)
                Reconnect($"no media for {silent / 1000} s");
        }

        /// <summary>Connects again, once at a time, every <see cref="RetryDelay"/> until it is, or until disposed.</summary>
        private void Reconnect(string reason)
        {
            if (_disposed || Interlocked.Exchange(ref _reconnecting, 1) == 1)
                return;
            Trace?.Invoke($"Reconnecting: {reason}");

            Task.Run(async () =>
            {
                try
                {
                    CloseConnection();
                    while (!_disposed)
                    {
                        // a new stream, of new clocks, from its first key frame
                        _resynchronizing = true;
                        RestartTimeline();
                        await Task.Delay(RetryDelay);
                        if (_disposed)
                            return;
                        try
                        {
                            await ConnectAsync(KeyFrameTimeout);
                            Trace?.Invoke("Reconnected");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Trace?.Invoke($"Reconnecting failed: {ex.Message}");
                            CloseConnection();
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _lastRtpAt, Environment.TickCount64);
                    Volatile.Write(ref _reconnecting, 0);
                }
            });
        }

        #endregion // Connecting

        /// <summary>The depacketizer of a format: SharpRTSP's, as RTSP's source has them.</summary>
        private static IPayloadProcessor CreateProcessor(RtpFormat format) => format.Name.ToUpperInvariant() switch
        {
            "H264" => new H264Payload(),
            // no DONL: WebRTC's receivers do not read one, so RTSPtoWebRTC takes a camera's out before it forwards the H.265
            "H265" => new H265Payload(false),
            "AV1" => new AV1Payload(),
            "VP9" => new VP9Payload(),
            "OPUS" => new OpusPayload(),
            "PCMU" or "PCMA" => new G711Payload(),
            _ => throw new NotSupportedException($"RTP payload {format.Name}")
        };

        private static AudioInfo CreateAudioInfo(MediaTrack track)
        {
            var format = track?.Formats.FirstOrDefault();
            if (format == null)
                return null;

            if (format.Name.Equals("opus", StringComparison.OrdinalIgnoreCase))
            {
                return new AudioInfo
                {
                    AudioCodec = "OPUS",
                    BitsPerSample = 32,
                    ChannelCount = 2,
                    SampleRate = 48000,
                };
            }

            // G.711: of 8 bit samples, companded, expanded to 16 bit PCM
            return new AudioInfo
            {
                AudioCodec = "PCM",
                BitsPerSample = 16,
                ChannelCount = (uint)Math.Max(1, format.Channels),
                SampleRate = (uint)format.ClockRate,
                PcmFormat = new PcmFormat
                {
                    BitsPerSample = 8,
                    Companding = format.Name == "PCMA" ? PcmCompanding.ALaw : PcmCompanding.MuLaw,
                },
            };
        }

        #region Receiving

        private void OnRtcp(object sender, RtcpPacketEventArgs e)
        {
            if (sender != _connection)
                return;
            // a report of a sender that has sent nothing yet is of no stream, and not taken: see ChannelState
            foreach (var report in e.SenderReports)
                _senderClocks.GetOrAdd(report.Ssrc, _ => new ChannelState()).RecordSenderReport(report.NtpTime, report.RtpTimestamp, report.PacketCount);
        }

        /// <summary>A packet's time on its sender's clock, of the last report of its source; false of none yet.</summary>
        private bool TryGetSenderTime(uint ssrc, uint rtpTimestamp, int clockRate, out DateTime senderTime)
        {
            senderTime = default;
            if (!_senderClocks.TryGetValue(ssrc, out var clock))
                return false;
            clock.ClockRate = clockRate;
            return clock.TryMapToSenderClock(rtpTimestamp, out senderTime);
        }

        private void OnRtp(object sender, RtpPacketEventArgs e)
        {
            if (sender != _connection)
                return; // of a session let go of
            Interlocked.Exchange(ref _lastRtpAt, Environment.TickCount64);
            if (e.Track == _videoTrack)
                OnVideo((PeerConnection)sender, e);
            else if (e.Track == _audioTrack)
                OnAudio(e);
        }

        private void OnVideo(PeerConnection connection, RtpPacketEventArgs e)
        {
            if (_videoProcessor == null)
            {
                _videoProcessor = CreateProcessor(e.Format);
                _videoCodec = e.Format.Name.ToUpperInvariant();
            }
            if (!_keyFrameRequested)
            {
                // what comes before a key frame does not decode: one asked for now, rather than at the sender's next
                _keyFrameRequested = true;
                connection.RequestKeyFrame(_videoTrack);
            }

            using (RawMediaFrame frames = _videoProcessor.ProcessPacket(new RtpPacket(e.Buffer.AsSpan(e.Offset, e.Count))))
            {
                if (!frames.Any())
                    return;

                var sample = RentUnits();
                foreach (var unit in frames.Data)
                {
                    sample.Add(unit.Span);
                    if (IsParameterSet(_videoCodec, unit.Span))
                        KeepParameterSet(unit.Span);
                }

                if (_videoInfo == null || _resynchronizing)
                {
                    // nothing before the picture's size is known: the decoder is made of it, and what precedes it does not
                    // decode - nor, of a session connected again, what precedes its first key frame
                    if (!TryReadSize(_videoCodec, sample.Units, out VideoInfo sized))
                    {
                        if (Trace != null && ++_unsized % 25 == 1)
                            Trace($"Video of no size yet: {_unsized} frames, the last of units {string.Join(" ", sample.Units.Select(u => NalUnitType(_videoCodec, u)))}");
                        sample.Release();
                        return;
                    }
                    if (_videoInfo == null)
                    {
                        _videoInfo = sized;
                    }
                    else if (sized.VideoCodec != _videoInfo.VideoCodec || sized.OriginalWidth != _videoInfo.OriginalWidth || sized.OriginalHeight != _videoInfo.OriginalHeight)
                    {
                        // the decoder is of the first session's: of another codec, or size, it does not decode as it should
                        Trace?.Invoke($"The stream connected again is {sized.VideoCodec} {sized.OriginalWidth}x{sized.OriginalHeight}, not {_videoInfo.VideoCodec} {_videoInfo.OriginalWidth}x{_videoInfo.OriginalHeight}");
                    }
                    _resynchronizing = false;
                    _videoSized.TrySetResult(true);
                }

                bool synced = TryGetSenderTime(e.Ssrc, frames.RtpTimestamp, e.Format.ClockRate, out DateTime senderTime);
                sample.Timestamp = VideoTime(frames.RtpTimestamp, synced, senderTime);
                _videoSampleQueue.Enqueue(sample);
            }
        }

        // the stream's parameter sets, in the order of their types - VPS, SPS, PPS - kept for the decoder drained
        private void KeepParameterSet(ReadOnlySpan<byte> unit)
        {
            _inBandParameterSets[NalUnitType(_videoCodec, unit)] = unit.ToArray();
            _streamParameterSets = _inBandParameterSets.Values.ToArray();
        }

        private void OnAudio(RtpPacketEventArgs e)
        {
            _audioProcessor ??= CreateProcessor(e.Format);
            using (RawMediaFrame frames = _audioProcessor.ProcessPacket(new RtpPacket(e.Buffer.AsSpan(e.Offset, e.Count))))
            {
                // the sound from where the video is taken, of a session that has it
                if (!frames.Any() || (_videoInfo == null || _resynchronizing) && _videoTrack != null)
                    return;

                // the time of the packet's first frame: that of the rest follows from the frames decoded before them
                bool synced = TryGetSenderTime(e.Ssrc, frames.RtpTimestamp, e.Format.ClockRate, out DateTime senderTime);
                long time = AudioTime(synced, senderTime);
                foreach (var unit in frames.Data)
                {
                    var frame = RentUnits();
                    frame.Add(unit.Span);
                    frame.Timestamp = time;
                    time = -1;
                    _audioSampleQueue.Enqueue(frame);
                }
            }
        }

        #endregion // Receiving

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _watchdog?.Dispose();
                CloseConnection();
            }
            base.Dispose(disposing);
        }
    }
}
