using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.Media;
using Windows.Media.Playback;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using System.Threading.Tasks;
using System.Threading;

namespace AudioPlayerTask
{
    public sealed class AudioTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;
        private SystemMediaTransportControls _systemControls;
        private MediaPlayer _mediaPlayer;

        // [OPT] Shared HttpClient — avoids socket leaks from creating new instances per resolve call
        private Windows.Web.Http.HttpClient _httpClient = new Windows.Web.Http.HttpClient();

        private List<string> _trackList = new List<string>();
        private List<string> _titleList = new List<string>();
        private List<string> _artistList = new List<string>();
        private List<string> _videoIdList = new List<string>();
        private List<string> _thumbnailList = new List<string>();

        private int _currentTrackIndex = -1;
        private Random _rand = new Random();
        private int _retryCount = 0;
        private bool _isRetrying = false;
        private string _currentLoadedVidId = "";

        // Server stream state
        private string _resolvedUrl = null;
        private bool _innerTubeAttempted = false;
        private TimeSpan _pendingStartPosition = TimeSpan.Zero;
        private int _playbackGeneration = 0;
        private bool _foregroundVideoMode = false;
        private bool _foregroundVideoPlaying = false;
        private bool _backgroundPlaybackRequested = true;
        private long _lastPlaybackCommandGeneration = 0;
        private long _lastTransportCommandGeneration = 0;
        private long _lastPlaylistRequestId = 0;
        private long _awaitingPlaylistGeneration = 0;
        private int _pendingTrackOffset = 0;
        private bool _pendingRestartRequested = false;
        private bool _playlistNeedsStart = false;

        // Tối đa 4 lần retry: Stream URL (2 lần) → Render /api/play (2 lần)
        private const int MAX_RETRIES = 4;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();

            _systemControls = SystemMediaTransportControls.GetForCurrentView();
            _systemControls.IsEnabled = true;
            _systemControls.ButtonPressed += SystemControls_ButtonPressed;
            _systemControls.IsPlayEnabled = true;
            _systemControls.IsPauseEnabled = true;
            _systemControls.IsStopEnabled = true;
            _systemControls.IsNextEnabled = true;
            _systemControls.IsPreviousEnabled = true;

            _mediaPlayer = BackgroundMediaPlayer.Current;
            _foregroundVideoMode = ReadPersistedForegroundVideoMode();
            _backgroundPlaybackRequested = !_foregroundVideoMode;
            // A foreground MediaElement cannot still be playing when this task is cold-started.
            _foregroundVideoPlaying = false;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.CurrentStateChanged += MediaPlayer_CurrentStateChanged;

            BackgroundMediaPlayer.MessageReceivedFromForeground += BackgroundMediaPlayer_MessageReceivedFromForeground;
            taskInstance.Canceled += TaskInstance_Canceled;
            try { BackgroundMediaPlayer.SendMessageToForeground(new ValueSet { { "BackgroundReady", "" } }); } catch { }
        }

        private void TaskInstance_Canceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            try
            {
                _systemControls.ButtonPressed -= SystemControls_ButtonPressed;
                _systemControls.IsEnabled = false;
                _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
                _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
                _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                _mediaPlayer.CurrentStateChanged -= MediaPlayer_CurrentStateChanged;
                BackgroundMediaPlayer.MessageReceivedFromForeground -= BackgroundMediaPlayer_MessageReceivedFromForeground;
                BackgroundMediaPlayer.Shutdown();
                StopCrossfadeMonitor();
                _httpClient?.Dispose();
            }
            catch { }
            if (_deferral != null) _deferral.Complete();
        }

        private void BackgroundMediaPlayer_MessageReceivedFromForeground(object sender, MediaPlayerDataReceivedEventArgs e)
        {
            if (e.Data.ContainsKey("Ping"))
            {
                try { BackgroundMediaPlayer.SendMessageToForeground(new ValueSet { { "BackgroundReady", "" } }); } catch { }
            }
            else if (e.Data.ContainsKey("SetForegroundVideoMode"))
            {
                long commandGeneration = GetCommandGeneration(e.Data);
                bool requestedVideoMode = (bool)e.Data["SetForegroundVideoMode"];
                bool duplicateCommand = commandGeneration > 0 &&
                    commandGeneration == _lastPlaybackCommandGeneration &&
                    requestedVideoMode == _foregroundVideoMode;
                if (!AcceptCommandGeneration(commandGeneration)) return;
                _foregroundVideoMode = requestedVideoMode;
                _foregroundVideoPlaying = e.Data.ContainsKey("VideoIsPlaying") && (bool)e.Data["VideoIsPlaying"];
                if (duplicateCommand)
                {
                    _systemControls.PlaybackStatus = _foregroundVideoMode
                        ? (_foregroundVideoPlaying
                            ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused)
                        : (_mediaPlayer.CurrentState == MediaPlayerState.Playing
                            ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused);
                    return;
                }
                _playbackGeneration++;
                StopCrossfadeMonitor();
                if (_foregroundVideoMode)
                {
                    _backgroundPlaybackRequested = false;
                    _awaitingPlaylistGeneration = 0;
                    _pendingTrackOffset = 0;
                    _pendingRestartRequested = false;
                    try { _mediaPlayer.Pause(); } catch { }
                    _systemControls.PlaybackStatus = _foregroundVideoPlaying
                        ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                }
                else
                {
                    bool playlistWillFollow = e.Data.ContainsKey("PlaylistWillFollow") &&
                        (bool)e.Data["PlaylistWillFollow"];
                    if (commandGeneration > 0 && playlistWillFollow)
                    {
                        _awaitingPlaylistGeneration = commandGeneration;
                        _pendingTrackOffset = 0;
                        _pendingRestartRequested = false;
                    }
                    _systemControls.PlaybackStatus = _mediaPlayer.CurrentState == MediaPlayerState.Playing
                        ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                }
            }
            else if (e.Data.ContainsKey("VideoPlaybackState"))
            {
                if (IsForegroundVideoMode())
                {
                    _foregroundVideoMode = true;
                    _foregroundVideoPlaying = (bool)e.Data["VideoPlaybackState"];
                    _systemControls.PlaybackStatus = _foregroundVideoPlaying
                        ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                }
            }
            else if (e.Data.ContainsKey("UpdatePlaylist"))
            {
                long playlistRequestId = 0;
                try
                {
                    if (e.Data.ContainsKey("PlaylistRequestId"))
                        playlistRequestId = Convert.ToInt64(e.Data["PlaylistRequestId"]);
                }
                catch { }
                if (playlistRequestId > 0 && playlistRequestId <= _lastPlaylistRequestId)
                {
                    SendPlaylistAccepted(playlistRequestId);
                    return;
                }

                long commandGeneration = GetCommandGeneration(e.Data);
                bool isAwaitedPlaylist = commandGeneration > 0 &&
                    commandGeneration == _awaitingPlaylistGeneration;
                bool commandIsCurrent = commandGeneration <= 0 ||
                    commandGeneration >= _lastPlaybackCommandGeneration;
                if (!commandIsCurrent && !isAwaitedPlaylist)
                {
                    if (playlistRequestId > 0) _lastPlaylistRequestId = playlistRequestId;
                    SendPlaylistAccepted(playlistRequestId);
                    return;
                }
                if (commandIsCurrent)
                {
                    if (!AcceptCommandGeneration(commandGeneration)) return;
                    if (commandGeneration <= 0 ||
                        _lastTransportCommandGeneration <= commandGeneration)
                        _backgroundPlaybackRequested = true;
                }
                if (commandIsCurrent || isAwaitedPlaylist)
                {
                    _foregroundVideoMode = false;
                    _foregroundVideoPlaying = false;
                    _playbackGeneration++;
                    StopCrossfadeMonitor();
                }

                _systemControls.IsEnabled = true;
                _systemControls.IsPlayEnabled = true;
                _systemControls.IsPauseEnabled = true;
                _systemControls.IsStopEnabled = true;
                _systemControls.IsNextEnabled = true;
                _systemControls.IsPreviousEnabled = true;
                _trackList = new List<string>((string[])e.Data["Urls"]);
                _titleList = new List<string>((string[])e.Data["Titles"]);
                _artistList = new List<string>((string[])e.Data["Artists"]);
                _videoIdList = new List<string>((string[])e.Data["VideoIds"]);
                _thumbnailList = new List<string>((string[])e.Data["Thumbnails"]);
                _currentTrackIndex = (int)e.Data["StartIndex"];
                bool playlistIndexChanged = _pendingTrackOffset != 0;
                if (_pendingTrackOffset != 0 && _trackList.Count > 0)
                {
                    _currentTrackIndex += _pendingTrackOffset;
                    while (_currentTrackIndex < 0) _currentTrackIndex += _trackList.Count;
                    while (_currentTrackIndex >= _trackList.Count) _currentTrackIndex -= _trackList.Count;
                    _pendingTrackOffset = 0;
                }
                _pendingStartPosition = e.Data.ContainsKey("StartPositionTicks")
                    ? TimeSpan.FromTicks((long)e.Data["StartPositionTicks"])
                    : TimeSpan.Zero;
                if (playlistIndexChanged)
                    _pendingStartPosition = TimeSpan.Zero;
                if (_pendingRestartRequested)
                {
                    _pendingStartPosition = TimeSpan.Zero;
                    _pendingRestartRequested = false;
                }
                if (isAwaitedPlaylist || commandIsCurrent)
                    _awaitingPlaylistGeneration = 0;
                _playlistNeedsStart = true;

                _innerTubeAttempted = false;
                if (e.Data.ContainsKey("FastUrl") && !playlistIndexChanged)
                {
                    string fastUrl = e.Data["FastUrl"].ToString();
                    if (!string.IsNullOrEmpty(fastUrl) && _currentTrackIndex < _trackList.Count)
                    {
                        _trackList[_currentTrackIndex] = fastUrl;
                        // Foreground đã resolve → skip InnerTube trong AudioTask
                        _innerTubeAttempted = true;
                    }
                }

                bool hasFastUrl = _innerTubeAttempted; // set true bởi FastUrl ở trên
                ResetRetryState();
                if (hasFastUrl) _innerTubeAttempted = true; // giữ lại → skip double-resolve
                if (playlistRequestId > 0) _lastPlaylistRequestId = playlistRequestId;
                SendPlaylistAccepted(playlistRequestId);
                if (!IsForegroundVideoMode() && _backgroundPlaybackRequested)
                    StartPlaybackAsync();
            }
            else if (e.Data.ContainsKey("UpdateQueueOnly"))
            {
                _trackList = new List<string>((string[])e.Data["Urls"]);
                _titleList = new List<string>((string[])e.Data["Titles"]);
                _artistList = new List<string>((string[])e.Data["Artists"]);
                _videoIdList = new List<string>((string[])e.Data["VideoIds"]);
                _thumbnailList = new List<string>((string[])e.Data["Thumbnails"]);
                if (e.Data.ContainsKey("CurrentIndex"))
                {
                    _currentTrackIndex = (int)e.Data["CurrentIndex"];
                }
                PreResolveNextTrack();
            }
            else if (e.Data.ContainsKey("ResumePlaybackMessage"))
            {
                long transportGeneration;
                if (!TryAcceptTransportCommand(e.Data, out transportGeneration)) return;
                _foregroundVideoMode = false;
                _foregroundVideoPlaying = false;
                _backgroundPlaybackRequested = true;
                if (_awaitingPlaylistGeneration > 0)
                {
                    SendTransportAccepted(transportGeneration);
                    return;
                }
                try
                {
                    if (_playlistNeedsStart ||
                        _mediaPlayer.CurrentState == MediaPlayerState.Closed ||
                        _mediaPlayer.CurrentState == MediaPlayerState.Stopped)
                        StartPlaybackAsync();
                    else
                    {
                        _mediaPlayer.Play();
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    }
                }
                catch { StartPlaybackAsync(); }
                SendTransportAccepted(transportGeneration);
            }
            else if (e.Data.ContainsKey("NextTrackMessage"))
            {
                long transportGeneration;
                if (!TryAcceptTransportCommand(e.Data, out transportGeneration)) return;
                _foregroundVideoMode = false;
                _foregroundVideoPlaying = false;
                _backgroundPlaybackRequested = true;
                if (_awaitingPlaylistGeneration > 0 || _trackList.Count == 0)
                {
                    _pendingTrackOffset++;
                    SendTransportAccepted(transportGeneration);
                    return;
                }
                MoveNext();
                SendTransportAccepted(transportGeneration);
            }
            else if (e.Data.ContainsKey("PrevTrackMessage"))
            {
                long transportGeneration;
                if (!TryAcceptTransportCommand(e.Data, out transportGeneration)) return;
                _foregroundVideoMode = false;
                _foregroundVideoPlaying = false;
                _backgroundPlaybackRequested = true;
                if (_awaitingPlaylistGeneration > 0 || _trackList.Count == 0)
                {
                    _pendingTrackOffset--;
                    SendTransportAccepted(transportGeneration);
                    return;
                }
                MovePrevious();
                SendTransportAccepted(transportGeneration);
            }
            else if (e.Data.ContainsKey("RestartTrackMessage"))
            {
                long transportGeneration;
                if (!TryAcceptTransportCommand(e.Data, out transportGeneration)) return;
                _foregroundVideoMode = false;
                _foregroundVideoPlaying = false;
                _backgroundPlaybackRequested = true;
                if (_awaitingPlaylistGeneration > 0)
                {
                    _pendingRestartRequested = true;
                    _pendingTrackOffset = 0;
                    SendTransportAccepted(transportGeneration);
                    return;
                }
                _playbackGeneration++;
                ResetRetryState();
                try
                {
                    if (_playlistNeedsStart ||
                        _mediaPlayer.CurrentState == MediaPlayerState.Closed ||
                        _mediaPlayer.CurrentState == MediaPlayerState.Stopped)
                    {
                        _pendingStartPosition = TimeSpan.Zero;
                        StartPlaybackAsync();
                    }
                    else
                    {
                        _mediaPlayer.Position = TimeSpan.Zero;
                        _mediaPlayer.Play();
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    }
                }
                catch { StartPlaybackAsync(); }
                SendTransportAccepted(transportGeneration);
            }
            else if (e.Data.ContainsKey("PausePlaybackMessage"))
            {
                long transportGeneration;
                if (!TryAcceptTransportCommand(e.Data, out transportGeneration)) return;
                try
                {
                    _foregroundVideoMode = false;
                    _foregroundVideoPlaying = false;
                    _backgroundPlaybackRequested = false;
                    _playbackGeneration++;
                    _isRetrying = false;
                    StopCrossfadeMonitor();
                    _mediaPlayer.Pause();
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                }
                catch { }
                SendTransportAccepted(transportGeneration);
            }
            else if (e.Data.ContainsKey("StopPlaybackMessage"))
            {
                long transportGeneration;
                if (!TryAcceptTransportCommand(e.Data, out transportGeneration)) return;
                try
                {
                    _foregroundVideoMode = false;
                    _foregroundVideoPlaying = false;
                    _backgroundPlaybackRequested = false;
                    _playbackGeneration++;
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values["BackgroundWasPlaying"] = false;
                    StopCrossfadeMonitor();
                    _mediaPlayer.Pause();
                    _trackList.Clear();
                    _titleList.Clear();
                    _artistList.Clear();
                    _videoIdList.Clear();
                    _thumbnailList.Clear();
                    _currentTrackIndex = -1;
                    _awaitingPlaylistGeneration = 0;
                    _pendingTrackOffset = 0;
                    _pendingRestartRequested = false;
                    _playlistNeedsStart = false;
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    _systemControls.IsEnabled = false;
                    var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                    settings.Remove("CurrentTitle");
                    settings.Remove("CurrentArtist");
                    settings.Remove("CurrentVideoId");
                    settings.Remove("CurrentThumbnail");
                }
                catch { }
                SendTransportAccepted(transportGeneration);
                try { BackgroundMediaPlayer.Shutdown(); } catch { }
            }
        }

        private bool ReadPersistedForegroundVideoMode()
        {
            try
            {
                var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                return values.ContainsKey("PlaybackSurfaceMode") &&
                    values["PlaybackSurfaceMode"] != null &&
                    values["PlaybackSurfaceMode"].ToString() == "Video";
            }
            catch
            {
                return _foregroundVideoMode;
            }
        }

        private bool IsForegroundVideoMode()
        {
            return _foregroundVideoMode;
        }

        private long GetCommandGeneration(ValueSet data)
        {
            try
            {
                if (data.ContainsKey("PlaybackCommandGeneration"))
                    return Convert.ToInt64(data["PlaybackCommandGeneration"]);
            }
            catch { }
            return 0;
        }

        private bool AcceptCommandGeneration(long generation)
        {
            if (generation <= 0)
            {
                generation = DateTime.UtcNow.Ticks;
                if (generation <= _lastPlaybackCommandGeneration)
                    generation = _lastPlaybackCommandGeneration + 1;
            }
            if (generation < _lastPlaybackCommandGeneration) return false;
            _lastPlaybackCommandGeneration = generation;
            return true;
        }

        private void AdvanceInternalTransportGeneration()
        {
            long generation = DateTime.UtcNow.Ticks;
            if (generation <= _lastTransportCommandGeneration)
                generation = _lastTransportCommandGeneration + 1;
            if (generation <= _lastPlaybackCommandGeneration)
                generation = _lastPlaybackCommandGeneration + 1;
            _lastTransportCommandGeneration = generation;
        }

        private void SendPlaylistAccepted(long requestId)
        {
            try
            {
                var message = new ValueSet { { "PlaylistAccepted", "" } };
                if (requestId > 0) message.Add("PlaylistRequestId", requestId);
                BackgroundMediaPlayer.SendMessageToForeground(message);
            }
            catch { }
        }

        private bool TryAcceptTransportCommand(ValueSet data, out long generation)
        {
            generation = GetCommandGeneration(data);
            if (generation <= 0)
            {
                generation = DateTime.UtcNow.Ticks;
                if (generation <= _lastTransportCommandGeneration)
                    generation = _lastTransportCommandGeneration + 1;
            }
            if (generation < _lastPlaybackCommandGeneration ||
                generation <= _lastTransportCommandGeneration)
            {
                SendTransportAccepted(generation);
                return false;
            }
            _lastTransportCommandGeneration = generation;
            return true;
        }

        private void SendTransportAccepted(long generation)
        {
            try
            {
                var message = new ValueSet { { "TransportAccepted", "" } };
                if (generation > 0) message.Add("PlaybackCommandGeneration", generation);
                BackgroundMediaPlayer.SendMessageToForeground(message);
            }
            catch { }
        }

        private bool CanStartBackgroundPlayback(int generation)
        {
            return generation == _playbackGeneration && !IsForegroundVideoMode() &&
                _backgroundPlaybackRequested;
        }

        private void ResetRetryState()
        {
            _retryCount = 0;
            _isRetrying = false;
            _resolvedUrl = null;
            _innerTubeAttempted = false;
        }

        // ==========================================
        // RESOLVE AUDIO URL — InnerTube direct (ANDROID_VR)
        // ==========================================
        private string _innerTubeDebug = "";

        /// <summary>
        /// Lấy visitorData — cache + LocalSettings + sw.js_data endpoint (~2KB, an toàn RAM cho background task)
        /// </summary>
        private static string _cachedVisitorData = null;

        private async Task<string> GetVisitorDataAsync(string videoId = null)
        {
            if (!string.IsNullOrEmpty(_cachedVisitorData))
                return _cachedVisitorData;

            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                if (settings.ContainsKey("CachedVisitorData"))
                {
                    string savedVd = settings["CachedVisitorData"] as string;
                    if (!string.IsNullOrEmpty(savedVd) && savedVd.StartsWith("Cg"))
                    {
                        _cachedVisitorData = savedVd;
                        return savedVd;
                    }
                }
            }
            catch { }

            string vd = await FetchVisitorDataFromSwJs();
            if (!string.IsNullOrEmpty(vd))
            {
                _cachedVisitorData = vd;
                try
                {
                    Windows.Storage.ApplicationData.Current.LocalSettings.Values["CachedVisitorData"] = vd;
                }
                catch { }
                return vd;
            }

            return null;
        }

        private async Task<string> FetchVisitorDataFromSwJs()
        {
            try
            {
                var request = new Windows.Web.Http.HttpRequestMessage(Windows.Web.Http.HttpMethod.Get,
                    new Uri("https://www.youtube.com/sw.js_data"));
                request.Headers.TryAppendWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Linux; Andr0id 9; BRAVIA 8K UR2) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/84.0.4147.125 Safari/537.36 OPR/46.0.2207.0 OMI/4.21.0.273.DIA6.149 Model/Sony-BRAVIA-8K-UR2,gzip(gfe)");
                request.Headers.Add("Accept", "application/json");

                string result;
                using (var response = await _httpClient.SendRequestAsync(request))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    result = await response.Content.ReadAsStringAsync();
                }

                if (result.StartsWith(")]}'"))
                    result = result.Substring(4);

                return ExtractVisitorData(result);
            }
            catch { return null; }
        }

        private string ExtractVisitorData(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // 1. Tìm visitorData":"CgXXX"
            string[] markers = { "visitorData\":\"", "\"visitorData\":\"" };
            foreach (string marker in markers)
            {
                int pos = text.IndexOf(marker);
                if (pos >= 0)
                {
                    int start = pos + marker.Length;
                    int end = text.IndexOf("\"", start);
                    if (end > start && end - start >= 20 && end - start < 600)
                    {
                        string vd = System.Net.WebUtility.UrlDecode(text.Substring(start, end - start));
                        if (vd.StartsWith("Cg")) return vd;
                    }
                }
            }

            // 2. Tìm "CgXXX" trong array format của sw.js_data
            int searchPos = 0;
            while (searchPos < text.Length)
            {
                int quotePos = text.IndexOf("\"Cg", searchPos);
                if (quotePos < 0) break;

                int start2 = quotePos + 1; // skip opening quote
                int end2 = text.IndexOf("\"", start2);
                if (end2 > start2)
                {
                    int len = end2 - start2;
                    if (len >= 20 && len < 600)
                    {
                        string candidate = System.Net.WebUtility.UrlDecode(text.Substring(start2, len));
                        if (candidate.StartsWith("Cg"))
                        {
                            bool valid = true;
                            for (int i = 0; i < candidate.Length && valid; i++)
                            {
                                char c = candidate[i];
                                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                                      (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '=' || c == '+' || c == '/'))
                                    valid = false;
                            }
                            if (valid) return candidate;
                        }
                    }
                }
                searchPos = quotePos + 3;
            }

            return null;
        }

        private async Task<string> ResolveViaInnerTubeDirectAsync(string videoId)
        {
            _innerTubeDebug = "";
            
            // InnerTube ANDROID (giống LazyTube/MetroTube Patched)
            string url = await TryInnerTubeClient(videoId, "ANDROID", "20.49.37", "3", "Nokia", "LumiaWP", "11",
                "com.google.android.youtube/20.49.37 (Linux; U; Android 11) gzip");
            if (!string.IsNullOrEmpty(url)) return url;

            return null;
        }

        private async Task<string> TryInnerTubeClient(string videoId, string clientName, string clientVersion, 
            string clientId, string deviceMake, string deviceModel, string osVersion, string userAgent)
        {
            try
            {
                string visitorData = await GetVisitorDataAsync(videoId);
                string vdShort = visitorData != null ? visitorData.Substring(0, Math.Min(8, visitorData.Length)) : "NULL";

                string vdField = "";
                if (!string.IsNullOrEmpty(visitorData))
                    vdField = ",\"visitorData\":\"" + visitorData + "\"";

                string requestBody = "{" +
                    "\"contentCheckOk\":true," +
                    "\"context\":{\"client\":{" +
                        "\"clientName\":\"" + clientName + "\"," +
                        "\"clientVersion\":\"" + clientVersion + "\"," +
                        "\"deviceMake\":\"" + deviceMake + "\"," +
                        "\"deviceModel\":\"" + deviceModel + "\"," +
                        "\"userAgent\":\"" + userAgent + "\"," +
                        "\"osName\":\"Android\"," +
                        "\"osVersion\":\"" + osVersion + "\"," +
                        "\"platform\":\"MOBILE\"," +
                        "\"androidSdkVersion\":30," +
                        "\"hl\":\"en\"," +
                        "\"gl\":\"US\"," +
                        "\"clientFormFactor\":0" +
                        vdField +
                    "}}," +
                    "\"videoId\":\"" + videoId + "\"" +
                "}";

                var content = new Windows.Web.Http.HttpStringContent(
                    requestBody,
                    Windows.Storage.Streams.UnicodeEncoding.Utf8,
                    "application/json"
                );

                // [FIX] Use per-request headers instead of DefaultRequestHeaders to avoid race condition
                var request = new Windows.Web.Http.HttpRequestMessage(Windows.Web.Http.HttpMethod.Post,
                    new Uri("https://www.youtube.com/youtubei/v1/player?key=AIzaSyDSXy9qVx1CzG2S7hYy7G-F6-HQ8_kB4vI&prettyPrint=false&fields=playabilityStatus,streamingData"));
                request.Content = content;
                request.Headers.TryAppendWithoutValidation("User-Agent", userAgent);
                request.Headers.Add("X-YouTube-Client-Name", clientId);
                request.Headers.Add("X-YouTube-Client-Version", clientVersion);

                string json;
                using (var response = await _httpClient.SendRequestAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        _innerTubeDebug = clientName + ":HTTP" + (int)response.StatusCode;
                        return null;
                    }
                    json = await response.Content.ReadAsStringAsync();
                }

                Windows.Data.Json.JsonObject data;
                if (Windows.Data.Json.JsonObject.TryParse(json, out data))
                {
                    string status = "";
                    if (data.ContainsKey("playabilityStatus"))
                    {
                        var pStatus = data.GetNamedObject("playabilityStatus");
                        if (pStatus.ContainsKey("status"))
                            status = pStatus.GetNamedString("status");
                    }

                    if (status != "OK")
                    {
                        _innerTubeDebug = clientName + ":" + status;
                        return null;
                    }

                    int[] preferredItags = new[] { 140, 141, 139, 18 };

                    if (data.ContainsKey("streamingData"))
                    {
                        var streamingData = data.GetNamedObject("streamingData");
                        if (streamingData.ContainsKey("adaptiveFormats"))
                        {
                            var audioFormats = streamingData.GetNamedArray("adaptiveFormats");
                            foreach (int targetItag in preferredItags)
                            {
                                foreach (var fmtVal in audioFormats)
                                {
                                    if (fmtVal.ValueType != Windows.Data.Json.JsonValueType.Object) continue;
                                    var fmt = fmtVal.GetObject();
                                    if (fmt.ContainsKey("itag") &&
                                        (int)fmt.GetNamedNumber("itag") == targetItag &&
                                        fmt.ContainsKey("url"))
                                    {
                                        _innerTubeDebug = clientName + ":OK(audio)";
                                        return fmt.GetNamedString("url");
                                    }
                                }
                            }
                        }
                        // 1. Ưu tiên itag 18 từ formats (không bị bóp băng thông)
                        if (streamingData.ContainsKey("formats"))
                        {
                            var formats = streamingData.GetNamedArray("formats");
                            foreach (var fmtVal in formats)
                            {
                                if (fmtVal.ValueType == Windows.Data.Json.JsonValueType.Object)
                                {
                                    var fmt = fmtVal.GetObject();
                                    if (fmt.ContainsKey("itag"))
                                    {
                                        int itag = (int)fmt.GetNamedNumber("itag");
                                        if (itag == 18 && fmt.ContainsKey("url"))
                                        {
                                            _innerTubeDebug = clientName + ":OK(i18)";
                                            return fmt.GetNamedString("url");
                                        }
                                    }
                                }
                            }
                        }

                        // 2. Fallback xuống adaptiveFormats (âm thanh chuyên dụng, dễ bị bóp/403)
                        if (streamingData.ContainsKey("adaptiveFormats"))
                        {
                            var formats = streamingData.GetNamedArray("adaptiveFormats");
                            foreach (int targetItag in preferredItags)
                            {
                                foreach (var fmtVal in formats)
                                {
                                    if (fmtVal.ValueType == Windows.Data.Json.JsonValueType.Object)
                                    {
                                        var fmt = fmtVal.GetObject();
                                        if (fmt.ContainsKey("itag"))
                                        {
                                            int itag = (int)fmt.GetNamedNumber("itag");
                                            if (itag == targetItag && fmt.ContainsKey("url"))
                                            {
                                                _innerTubeDebug = clientName + ":OK";
                                                return fmt.GetNamedString("url");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                _innerTubeDebug = clientName + ":NO_URL";
                _cachedVisitorData = null;
                try { Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove("CachedVisitorData"); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                _innerTubeDebug = clientName + ":EX:" + ex.Message.Substring(0, Math.Min(30, ex.Message.Length));
                _cachedVisitorData = null;
                try { Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove("CachedVisitorData"); } catch { }
                return null;
            }
        }


        // ==========================================
        // MAIN PLAYBACK — InnerTube direct only
        // ==========================================
        private async void StartPlaybackAsync()
        {
            int generation = ++_playbackGeneration;
            _playlistNeedsStart = true;
            if (!CanStartBackgroundPlayback(generation)) return;
            if (_currentTrackIndex < 0 || _currentTrackIndex >= _trackList.Count ||
                _currentTrackIndex >= _videoIdList.Count) return;

            string vidId = _videoIdList[_currentTrackIndex];

            // Offline track → phát trực tiếp
            if (vidId.StartsWith("LOCAL:"))
            {
                PlayUrl(_trackList[_currentTrackIndex], vidId, generation);
                return;
            }

            // Skip nếu bài cũ vẫn đang phát (không retry)
            if (vidId == _currentLoadedVidId && _mediaPlayer.CurrentState != MediaPlayerState.Closed && _retryCount == 0)
            {
                try
                {
                    if (!CanStartBackgroundPlayback(generation)) return;
                    _mediaPlayer.Position = _pendingStartPosition > TimeSpan.Zero ? _pendingStartPosition : TimeSpan.Zero;
                    if (!CanStartBackgroundPlayback(generation)) return;
                    _mediaPlayer.Play();
                    _playlistNeedsStart = false;
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    UpdateSystemMediaControls();
                }
                catch { }
                return;
            }

            // Nếu đã có URL resolved (từ retry)
            if (!string.IsNullOrEmpty(_resolvedUrl))
            {
                string url = _resolvedUrl;
                _resolvedUrl = null;
                PlayUrl(url, vidId, generation);
                return;
            }

            if (!_innerTubeAttempted)
            {
                _innerTubeAttempted = true;
                UpdateSystemMediaControls();

                string directUrl = await ResolveViaInnerTubeDirectAsync(vidId);
                if (!CanStartBackgroundPlayback(generation)) return;
                if (!string.IsNullOrEmpty(directUrl))
                {
                    directUrl = PrepareStreamUrl(directUrl);
                    PlayUrl(directUrl, vidId, generation);
                    return;
                }
            }

            // FALLBACK: URL từ MainPage — nếu rỗng thì resolve InnerTube lần nữa
            string fallbackUrl = _trackList[_currentTrackIndex];
            if (string.IsNullOrEmpty(fallbackUrl))
            {
                fallbackUrl = await ResolveViaInnerTubeDirectAsync(vidId);
            }
            if (!CanStartBackgroundPlayback(generation)) return;
            if (!string.IsNullOrEmpty(fallbackUrl))
                PlayUrl(PrepareStreamUrl(fallbackUrl), vidId, generation);
            else
                ReportErrorToUI("No stream available");
        }

        /// <summary>
        /// Thêm params chống throttle vào googlevideo URL:
        /// - ratebypass=yes: bỏ giới hạn tốc độ
        /// - range=0-: ép server gửi toàn bộ audio trong 1 response (full buffer)
        /// </summary>
        private string PrepareStreamUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.Contains("googlevideo"))
                return url;
            if (!url.Contains("ratebypass"))
                url += "&ratebypass=yes";
            return url;
        }

        private void PlayUrl(string trackUrl, string vidId, int generation)
        {
            if (!CanStartBackgroundPlayback(generation)) return;
            _mediaPlayer.AutoPlay = false;
            try
            {
                StopCrossfadeMonitor();

                // Normalize Volume: set consistent volume level
                var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                bool normalize = ls.ContainsKey("NormalizeVolume") ? (bool)ls["NormalizeVolume"] : false;
                _mediaPlayer.Volume = normalize ? 0.75 : 1.0;

                UpdateSystemMediaControls();
                _mediaPlayer.SetUriSource(new Uri(trackUrl));
                if (!CanStartBackgroundPlayback(generation))
                {
                    try { _mediaPlayer.Pause(); } catch { }
                    return;
                }
                _currentLoadedVidId = vidId;
                _mediaPlayer.Play();
                _playlistNeedsStart = false;
                _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;

                // Start crossfade monitoring and gapless pre-resolve
                StartCrossfadeMonitor();
                PreResolveNextTrack();
            }
            catch (Exception ex)
            {
                ReportErrorToUI("Stream Error: " + ex.Message.Split('\n')[0]);
            }
        }

        private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            if (IsForegroundVideoMode())
            {
                _pendingStartPosition = TimeSpan.Zero;
                try { sender.Pause(); } catch { }
                return;
            }
            if (!_backgroundPlaybackRequested)
            {
                if (_pendingStartPosition > TimeSpan.Zero)
                {
                    try { sender.Position = _pendingStartPosition; } catch { }
                }
                try { sender.Pause(); } catch { }
                return;
            }
            if (_pendingStartPosition <= TimeSpan.Zero) return;
            try { sender.Position = _pendingStartPosition; }
            catch { }
        }

        // ==========================================
        // RETRY FLOW — InnerTube only
        // Retry 1-2: Lấy URL InnerTube mới (URL cũ hết hạn)
        // Retry 3-4: Dùng URL từ MainPage
        // ==========================================
        private async void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (IsForegroundVideoMode())
            {
                _isRetrying = false;
                return;
            }
            int failureGeneration = _playbackGeneration;
            if (!CanStartBackgroundPlayback(failureGeneration)) return;
            // [FIX-SOF] Guard against re-entrancy — prevents StackOverflowException
            if (_isRetrying) return;
            _isRetrying = true;

            _currentLoadedVidId = "";
            _retryCount++;

            string vidId = (_currentTrackIndex >= 0 && _currentTrackIndex < _videoIdList.Count)
                ? _videoIdList[_currentTrackIndex] : "";

            if (string.IsNullOrEmpty(vidId) || vidId.StartsWith("LOCAL:"))
            {
                ResetRetryState();
                ReportErrorToUI("Playback failed");
                return;
            }

            if (_retryCount > MAX_RETRIES)
            {
                ResetRetryState();
                string err = "All sources failed";
                if (args.ExtendedErrorCode != null) err += " (" + args.ExtendedErrorCode.HResult + ")";
                ReportErrorToUI(err);
                return;
            }

            // Retry 1-2: Lấy URL InnerTube MỚI
            if (_retryCount <= 2)
            {
                await Task.Delay(800);
                if (!CanStartBackgroundPlayback(failureGeneration)) { _isRetrying = false; return; }
                _isRetrying = false; // Allow next failure to re-enter
                _cachedVisitorData = null;
                string freshUrl = await ResolveViaInnerTubeDirectAsync(vidId);
                if (!CanStartBackgroundPlayback(failureGeneration)) { _isRetrying = false; return; }
                if (!string.IsNullOrEmpty(freshUrl))
                {
                    freshUrl = PrepareStreamUrl(freshUrl);
                    _resolvedUrl = freshUrl;
                    _innerTubeAttempted = true;
                    StartPlaybackAsync();
                    return;
                }
            }

            // Retry 3-4: Dùng URL từ MainPage
            await Task.Delay(800);
            if (!CanStartBackgroundPlayback(failureGeneration)) { _isRetrying = false; return; }
            _isRetrying = false; // Allow next failure to re-enter
            _innerTubeAttempted = true;
            _resolvedUrl = null;
            StartPlaybackAsync();
        }

        private void SendToast(string message)
        {
            try { BackgroundMediaPlayer.SendMessageToForeground(new ValueSet { { "ToastMessage", message } }); } catch { }
        }

        private void ReportErrorToUI(string errorDetail)
        {
            string title = (_currentTrackIndex >= 0 && _currentTrackIndex < _titleList.Count) ? _titleList[_currentTrackIndex] : "Beatora";
            try
            {
                var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                ls["CurrentTitle"] = title; ls["CurrentArtist"] = errorDetail;
                var msg = new ValueSet { { "TrackChanged", "" }, { "NewTitle", title }, { "NewArtist", errorDetail } };
                if (_currentTrackIndex >= 0 && _currentTrackIndex < _thumbnailList.Count) msg.Add("NewThumbnail", _thumbnailList[_currentTrackIndex]);
                BackgroundMediaPlayer.SendMessageToForeground(msg);
            }
            catch { }
            try { _systemControls.DisplayUpdater.MusicProperties.Title = title; _systemControls.DisplayUpdater.MusicProperties.Artist = errorDetail; _systemControls.DisplayUpdater.Update(); } catch { }
        }

        private void UpdateSystemMediaControls()
        {
            if (_currentTrackIndex < 0 || _currentTrackIndex >= _titleList.Count) return;
            string title = _titleList[_currentTrackIndex], artist = _artistList[_currentTrackIndex];
            string thumb = _thumbnailList[_currentTrackIndex], vidId = _videoIdList[_currentTrackIndex];

            try { _systemControls.DisplayUpdater.Type = MediaPlaybackType.Music; _systemControls.DisplayUpdater.MusicProperties.Title = title; _systemControls.DisplayUpdater.MusicProperties.Artist = artist; _systemControls.DisplayUpdater.Update(); } catch { }
            try
            {
                var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                // FIX Bug 12: Dùng ContainsKey trước — ApplicationDataContainer throws KeyNotFoundException nếu key chưa tồn tại
                string storedTitle = ls.ContainsKey("CurrentTitle") ? ls["CurrentTitle"]?.ToString() : null;
                string storedArtist = ls.ContainsKey("CurrentArtist") ? ls["CurrentArtist"]?.ToString() : null;
                string storedVid = ls.ContainsKey("CurrentVideoId") ? ls["CurrentVideoId"]?.ToString() : null;
                string storedThumb = ls.ContainsKey("CurrentThumbnail") ? ls["CurrentThumbnail"]?.ToString() : null;

                if (storedTitle != title) ls["CurrentTitle"] = title;
                if (storedArtist != artist) ls["CurrentArtist"] = artist;
                if (storedVid != vidId) ls["CurrentVideoId"] = vidId;
                if (storedThumb != thumb) ls["CurrentThumbnail"] = thumb;
            }
            catch { }
            try { BackgroundMediaPlayer.SendMessageToForeground(new ValueSet { { "TrackChanged", "" }, { "NewTitle", title }, { "NewArtist", artist }, { "NewVideoId", vidId }, { "NewThumbnail", thumb } }); } catch { }
        }

        // ── Crossfade & Gapless ──
        private Windows.System.Threading.ThreadPoolTimer _crossfadeTimer;
        private bool _isCrossfading = false;
        private string _preResolvedNextUrl = null;
        private int _preResolvedNextIndex = -1;

        private void StartCrossfadeMonitor()
        {
            StopCrossfadeMonitor();
            // Poll every 500ms to check if we're near end of track
            _crossfadeTimer = Windows.System.Threading.ThreadPoolTimer.CreatePeriodicTimer(
                CrossfadeTimer_Tick, TimeSpan.FromMilliseconds(500));
        }

        private void StopCrossfadeMonitor()
        {
            if (_crossfadeTimer != null)
            {
                _crossfadeTimer.Cancel();
                _crossfadeTimer = null;
            }
        }

        private void CrossfadeTimer_Tick(Windows.System.Threading.ThreadPoolTimer timer)
        {
            try
            {
                if (IsForegroundVideoMode()) { StopCrossfadeMonitor(); return; }
                if (_mediaPlayer == null) return;
                if (_mediaPlayer.CurrentState != MediaPlayerState.Playing) return;

                var pos = _mediaPlayer.Position;
                var naturalDuration = _mediaPlayer.NaturalDuration;
                try
                {
                    var playbackState = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                    playbackState["BackgroundPositionTicks"] = pos.Ticks;
                    playbackState["BackgroundWasPlaying"] = true;
                }
                catch { }
                if (_trackList.Count <= 1) return;
                if (naturalDuration == TimeSpan.Zero || naturalDuration.TotalSeconds < 10) return;

                double remaining = (naturalDuration - pos).TotalSeconds;

                // Gapless: Pre-resolve next track URL 15s before end
                if (remaining <= 15 && remaining > 10 && string.IsNullOrEmpty(_preResolvedNextUrl))
                {
                    PreResolveNextTrack();
                }

                // Crossfade: start fade out when remaining <= CrossfadeDuration
                var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                bool crossfade = ls.ContainsKey("Crossfade") ? (bool)ls["Crossfade"] : false;
                if (!crossfade) return;

                int crossfadeSec = ls.ContainsKey("CrossfadeDuration") ? (int)ls["CrossfadeDuration"] : 3;
                if (crossfadeSec <= 0) return;

                if (remaining <= crossfadeSec && !_isCrossfading)
                {
                    _isCrossfading = true;
                    StartCrossfadeTransition(crossfadeSec);
                }
            }
            catch { }
        }

        private async void StartCrossfadeTransition(int durationSec)
        {
            int generation = _playbackGeneration;
            double origVol = 1.0;
            try
            {
                if (!CanStartBackgroundPlayback(generation)) { _isCrossfading = false; return; }
                origVol = _mediaPlayer.Volume;
                int steps = durationSec * 10; // 100ms per step
                double volStep = origVol / steps;

                for (int i = 0; i < steps; i++)
                {
                    await Task.Delay(100);
                    if (!CanStartBackgroundPlayback(generation))
                    {
                        try { _mediaPlayer.Volume = origVol; } catch { }
                        _isCrossfading = false;
                        return;
                    }
                    try
                    {
                        double newVol = _mediaPlayer.Volume - volStep;
                        if (newVol < 0) newVol = 0;
                        _mediaPlayer.Volume = newVol;
                    }
                    catch { break; }
                }

                // Crossfade complete → move to next
                _isCrossfading = false;
                if (!CanStartBackgroundPlayback(generation))
                {
                    try { _mediaPlayer.Volume = origVol; } catch { }
                    return;
                }
                MoveNext();
            }
            catch
            {
                try { _mediaPlayer.Volume = origVol; } catch { }
                _isCrossfading = false;
            }
        }

        private async void PreResolveNextTrack()
        {
            int generation = _playbackGeneration;
            _preResolvedNextUrl = null;
            _preResolvedNextIndex = -1;
            try
            {
                if (!CanStartBackgroundPlayback(generation)) return;
                var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                bool gapless = ls.ContainsKey("GaplessPlayback") ? (bool)ls["GaplessPlayback"] : true;
                if (!gapless) return;
                if (_trackList.Count <= 1) return;

                bool shuffle = ls.ContainsKey("ShuffleMode") ? (bool)ls["ShuffleMode"] : false;
                int repeat = ls.ContainsKey("RepeatMode") ? (int)ls["RepeatMode"] : 0;
                bool autoplay = ls.ContainsKey("Autoplay") ? (bool)ls["Autoplay"] : true;

                int nextIdx;
                if (repeat == 2) nextIdx = _currentTrackIndex;
                else if (shuffle) nextIdx = _rand.Next(0, _trackList.Count);
                else
                {
                    nextIdx = _currentTrackIndex + 1;
                    if (nextIdx >= _trackList.Count)
                    {
                        if (repeat == 1 || autoplay) nextIdx = 0;
                        else return;
                    }
                }

                string nextVidId = _videoIdList[nextIdx];
                if (nextVidId.StartsWith("LOCAL:"))
                {
                    _preResolvedNextUrl = _trackList[nextIdx];
                    _preResolvedNextIndex = nextIdx;
                    return;
                }

                string url = await ResolveViaInnerTubeDirectAsync(nextVidId);
                if (!CanStartBackgroundPlayback(generation)) return;
                if (!string.IsNullOrEmpty(url))
                {
                    _preResolvedNextUrl = PrepareStreamUrl(url);
                    _preResolvedNextIndex = nextIdx;
                }
            }
            catch { }
        }

        private void MoveNext()
        {
            if (IsForegroundVideoMode()) return;
            if (_trackList.Count == 0) return;
            _backgroundPlaybackRequested = true;
            _pendingStartPosition = TimeSpan.Zero;
            var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            bool shuffle = ls.ContainsKey("ShuffleMode") ? (bool)ls["ShuffleMode"] : false;
            int repeat = ls.ContainsKey("RepeatMode") ? (int)ls["RepeatMode"] : 0;
            bool autoplay = ls.ContainsKey("Autoplay") ? (bool)ls["Autoplay"] : true;
            if (repeat == 2) { ResetRetryState(); StartPlaybackAsync(); return; }
            ResetRetryState();

            // Use pre-resolved URL if available (gapless)
            string preUrl = _preResolvedNextUrl;
            int preIdx = _preResolvedNextIndex;
            _preResolvedNextUrl = null;
            _preResolvedNextIndex = -1;

            if (preIdx >= 0 && preIdx < _trackList.Count && !string.IsNullOrEmpty(preUrl))
            {
                _currentTrackIndex = preIdx;
                _trackList[_currentTrackIndex] = preUrl;
                _innerTubeAttempted = true;
            }
            else if (shuffle)
            {
                _currentTrackIndex = _rand.Next(0, _trackList.Count);
            }
            else
            {
                _currentTrackIndex++;
                if (_currentTrackIndex >= _trackList.Count)
                {
                    if (repeat == 1 || autoplay)
                    {
                        _currentTrackIndex = 0;
                    }
                    else
                    {
                        _currentTrackIndex = _trackList.Count - 1;
                        return; // Stop playback when queue ends and autoplay is off
                    }
                }
            }

            StartPlaybackAsync();
        }

        private void MovePrevious()
        {
            if (IsForegroundVideoMode()) return;
            if (_trackList.Count == 0) return;
            _backgroundPlaybackRequested = true;
            try
            {
                if ((_mediaPlayer.CurrentState == MediaPlayerState.Playing ||
                    _mediaPlayer.CurrentState == MediaPlayerState.Paused) &&
                    _mediaPlayer.Position.TotalSeconds > 3)
                {
                    _mediaPlayer.Position = TimeSpan.Zero;
                    _mediaPlayer.Play();
                    return;
                }
            }
            catch { }
            _pendingStartPosition = TimeSpan.Zero;
            var ls = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            bool shuffle = ls.ContainsKey("ShuffleMode") ? (bool)ls["ShuffleMode"] : false;
            int repeat = ls.ContainsKey("RepeatMode") ? (int)ls["RepeatMode"] : 0;
            if (repeat == 2) { ResetRetryState(); StartPlaybackAsync(); return; }
            ResetRetryState();
            if (shuffle) _currentTrackIndex = _rand.Next(0, _trackList.Count);
            else { _currentTrackIndex--; if (_currentTrackIndex < 0) { if (repeat == 1) _currentTrackIndex = _trackList.Count - 1; else { _currentTrackIndex = 0; return; } } }
            StartPlaybackAsync();
        }

        private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            if (IsForegroundVideoMode() || !_backgroundPlaybackRequested) return;
            // If crossfade already triggered MoveNext, skip
            if (_isCrossfading) return;
            StopCrossfadeMonitor();
            MoveNext();
        }

        private void MediaPlayer_CurrentStateChanged(MediaPlayer sender, object args)
        {
            try
            {
                if (IsForegroundVideoMode())
                {
                    _foregroundVideoMode = true;
                    if (sender.CurrentState == MediaPlayerState.Playing)
                    {
                        try { sender.Pause(); } catch { }
                    }
                    try { Windows.Storage.ApplicationData.Current.LocalSettings.Values["BackgroundWasPlaying"] = false; } catch { }
                    _systemControls.PlaybackStatus = _foregroundVideoPlaying
                        ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    return;
                }
                if (!_backgroundPlaybackRequested && sender.CurrentState == MediaPlayerState.Playing)
                {
                    try { sender.Pause(); } catch { }
                    try { Windows.Storage.ApplicationData.Current.LocalSettings.Values["BackgroundWasPlaying"] = false; } catch { }
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                    return;
                }
                if (sender.CurrentState == MediaPlayerState.Playing)
                {
                    _pendingStartPosition = TimeSpan.Zero;
                    if (_crossfadeTimer == null) StartCrossfadeMonitor();
                    try { Windows.Storage.ApplicationData.Current.LocalSettings.Values["BackgroundWasPlaying"] = true; } catch { }
                    // [FIX-SOF] Only reset retryCount if NOT in a retry cycle
                    // Without this guard, player briefly entering Playing before failing
                    // would reset _retryCount → infinite retry → StackOverflow
                    if (!_isRetrying) _retryCount = 0;
                    _isRetrying = false;
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                    try
                    {
                        var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeGlyph);
                        ((XmlElement)badgeXml.SelectSingleNode("/badge")).SetAttribute("value", "playing");
                        BadgeUpdateManager.CreateBadgeUpdaterForApplication().Update(new BadgeNotification(badgeXml));
                    }
                    catch { }
                }
                else if (sender.CurrentState == MediaPlayerState.Paused)
                {
                    try { Windows.Storage.ApplicationData.Current.LocalSettings.Values["BackgroundWasPlaying"] = false; } catch { }
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                    try
                    {
                        var badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeGlyph);
                        ((XmlElement)badgeXml.SelectSingleNode("/badge")).SetAttribute("value", "paused");
                        BadgeUpdateManager.CreateBadgeUpdaterForApplication().Update(new BadgeNotification(badgeXml));
                    }
                    catch { }
                }
                else if (sender.CurrentState == MediaPlayerState.Closed || sender.CurrentState == MediaPlayerState.Stopped)
                {
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Closed;
                    try
                    {
                        BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear();
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void SystemControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            if (IsForegroundVideoMode())
            {
                AdvanceInternalTransportGeneration();
                _foregroundVideoMode = true;
                string command = null;
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                        command = "Play";
                        _foregroundVideoPlaying = true;
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                        break;
                    case SystemMediaTransportControlsButton.Pause:
                        command = "Pause";
                        _foregroundVideoPlaying = false;
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                        break;
                    case SystemMediaTransportControlsButton.Stop:
                        command = "Stop";
                        _foregroundVideoPlaying = false;
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        command = "Next";
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        command = "Previous";
                        break;
                }
                if (!string.IsNullOrEmpty(command))
                {
                    try
                    {
                        BackgroundMediaPlayer.SendMessageToForeground(
                            new ValueSet { { "VideoTransportCommand", command } });
                    }
                    catch { }
                }
                return;
            }

            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    AdvanceInternalTransportGeneration();
                    _backgroundPlaybackRequested = true;
                    if (_awaitingPlaylistGeneration > 0) break;
                    try
                    {
                        if (_playlistNeedsStart ||
                            _mediaPlayer.CurrentState == MediaPlayerState.Closed ||
                            _mediaPlayer.CurrentState == MediaPlayerState.Stopped)
                            StartPlaybackAsync();
                        else
                            _mediaPlayer.Play();
                    }
                    catch { StartPlaybackAsync(); }
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    AdvanceInternalTransportGeneration();
                    _backgroundPlaybackRequested = false;
                    _playbackGeneration++;
                    _isRetrying = false;
                    StopCrossfadeMonitor();
                    try
                    {
                        _mediaPlayer.Pause();
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                    }
                    catch { }
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    AdvanceInternalTransportGeneration();
                    _backgroundPlaybackRequested = false;
                    _playbackGeneration++;
                    _awaitingPlaylistGeneration = 0;
                    _pendingTrackOffset = 0;
                    _pendingRestartRequested = false;
                    _playlistNeedsStart = false;
                    try
                    {
                        Windows.Storage.ApplicationData.Current.LocalSettings.Values["BackgroundWasPlaying"] = false;
                        StopCrossfadeMonitor();
                        _mediaPlayer.Pause();
                        _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                        _trackList.Clear();
                        _currentTrackIndex = -1;
                    }
                    catch { }
                    break;
                case SystemMediaTransportControlsButton.Next:
                    AdvanceInternalTransportGeneration();
                    _backgroundPlaybackRequested = true;
                    if (_awaitingPlaylistGeneration > 0 || _trackList.Count == 0)
                        _pendingTrackOffset++;
                    else MoveNext();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    AdvanceInternalTransportGeneration();
                    _backgroundPlaybackRequested = true;
                    if (_awaitingPlaylistGeneration > 0 || _trackList.Count == 0)
                        _pendingTrackOffset--;
                    else MovePrevious();
                    break;
            }
        }
        private static string FormatSquareThumbnail(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.Contains("googleusercontent.com") || url.Contains("ggpht.com"))
            {
                int eqIdx = url.LastIndexOf("=");
                if (eqIdx > 0)
                    return url.Substring(0, eqIdx) + "=w480-h480-l90-rj";
                return url + "=w480-h480-l90-rj";
            }
            if (url.Contains("hqdefault.jpg"))
                return url.Replace("hqdefault.jpg", "mqdefault.jpg");
            if (url.Contains("sddefault.jpg"))
                return url.Replace("sddefault.jpg", "mqdefault.jpg");
            return url;
        }
    }
}
