using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace YTMusicWP
{
    public sealed partial class MainPage
    {
        private ValueSet _pendingBackgroundPlaylist;
        private ValueSet _pendingBackgroundTransportCommand;
        private ValueSet _pendingForegroundVideoModeCommand;
        private int _playTrackRequestGeneration;
        private int _videoTransitionGeneration;
        private long _lastPlaybackCommandGeneration;
        private long _currentImagePlaylistRequestId;
        private long _lastBackgroundPlaybackStateGeneration;
        private long _lastBackgroundPlaybackStateSequence;
        private string _backgroundPlaybackStateInstanceId = "";
        private bool _imagePlaybackRequested;
        private bool _backgroundPositionIsCurrent;
        private bool _imagePlaybackStartPending;

        private long NextPlaybackCommandGeneration()
        {
            long generation = DateTime.UtcNow.Ticks;
            if (generation <= _lastPlaybackCommandGeneration)
                generation = _lastPlaybackCommandGeneration + 1;
            _lastPlaybackCommandGeneration = generation;
            return generation;
        }

        private void SendPlaylistToBackground(ValueSet message)
        {
            SendPlaylistToBackground(message, NextPlaybackCommandGeneration());
        }

        private void SendPlaylistToBackground(ValueSet message, long commandGeneration)
        {
            try
            {
                if (_pendingBackgroundPlaylist != null &&
                    _pendingBackgroundPlaylist.ContainsKey("PlaylistRequestId") &&
                    Convert.ToInt64(_pendingBackgroundPlaylist["PlaylistRequestId"]) >
                        commandGeneration)
                    return;
            }
            catch { }
            message["PlaybackCommandGeneration"] = commandGeneration;
            message["PlaylistRequestId"] = commandGeneration;
            _currentImagePlaylistRequestId = commandGeneration;
            _backgroundPositionIsCurrent = false;
            _imagePlaybackStartPending = true;
            if (commandGeneration >= _lastPlaybackCommandGeneration)
            {
                _imagePlaybackRequested = true;
                _pendingBackgroundTransportCommand = null;
            }
            _pendingBackgroundPlaylist = message;
            try
            {
                BackgroundMediaPlayer.MessageReceivedFromBackground -= BackgroundMediaPlayer_MessageReceivedFromBackground;
                BackgroundMediaPlayer.MessageReceivedFromBackground += BackgroundMediaPlayer_MessageReceivedFromBackground;
                _appMediaPlayer = BackgroundMediaPlayer.Current;
                _appMediaPlayer.CurrentStateChanged -= BackgroundMediaPlayer_CurrentStateChanged;
                _appMediaPlayer.CurrentStateChanged += BackgroundMediaPlayer_CurrentStateChanged;
            }
            catch { }
            SendForegroundVideoMode(false, false, commandGeneration, true);
            try
            {
                BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "Ping", "" } });
            }
            catch { }
        }

        private async void PlayTrack(YouTubeTrack track)
        {
            if (track == null || string.IsNullOrEmpty(track.VideoId)) return;
            if (track.VideoId.StartsWith("CHANNEL:"))
            {
                OpenArtistProfile(track.VideoId.Substring(8), track.Title ?? track.ChannelName, true);
                return;
            }
            if (track.VideoId.StartsWith("PLAYLIST:"))
            {
                OpenYouTubePlaylist(track.VideoId.Substring(9), track.Title, track.ThumbnailUrl);
                return;
            }
            if (!track.VideoId.StartsWith("LOCAL:") && !IsInternetAvailable())
            {
                ShowToast("No Internet connection");
                return;
            }

            int playRequestGeneration = ++_playTrackRequestGeneration;
            long playbackCommandGeneration = NextPlaybackCommandGeneration();
            int playbackSurfaceTransitionGeneration = ++_videoTransitionGeneration;
            _videoPositionBeforeSuspend = TimeSpan.Zero;
            _pendingVideoStartPosition = TimeSpan.Zero;
            _backgroundPositionIsCurrent = false;
            _imagePlaybackStartPending = true;
            _showVideoSurface = false;
            _imagePlaybackRequested = true;
            _pendingBackgroundPlaylist = null;
            try { ApplicationData.Current.LocalSettings.Values["PlaybackSurfaceMode"] = "Image"; } catch { }
            SendForegroundVideoMode(false, false, playbackCommandGeneration, true);

            try
            {
                if (_appMediaPlayer == null || _appMediaPlayer.CurrentState == MediaPlayerState.Closed)
                {
                    _appMediaPlayer = BackgroundMediaPlayer.Current;
                    _appMediaPlayer.CurrentStateChanged -= BackgroundMediaPlayer_CurrentStateChanged;
                    _appMediaPlayer.CurrentStateChanged += BackgroundMediaPlayer_CurrentStateChanged;
                }
            }
            catch { }
            try { ForegroundVideo.Stop(); } catch { }
            ForegroundVideo.Source = null;

            currentTrack = track;
            MiniPlayerContainer.Visibility = Visibility.Visible;
            MiniTitle.Text = track.Title; BigTitle.Text = track.Title;
            // Start marquee only if NowPlaying is already open (otherwise it starts when panel opens)
            if (NowPlayingView.Visibility == Visibility.Visible)
            {
                var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => StartTitleMarquee());
            }
            MiniArtist.Text = track.ChannelName; BigArtist.Text = track.ChannelName;
            SetPlayPauseIcon(true);
            MenuTitle.Text = track.Title;
            MenuArtist.Text = track.ChannelName;

            if (!string.IsNullOrEmpty(track.ThumbnailUrl))
            {
                // [OPT-M3] Dùng chung 1 BitmapImage cho BigCover + MenuCover (cùng src, cùng DecodePixelWidth)
                var bigBmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(GetNowPlayingThumbnail(track.ThumbnailUrl), UriKind.Absolute));
                bigBmp.DecodePixelWidth = 480;
                BigCoverImage.ImageSource  = bigBmp;
                AlbumArtEntranceStoryboard.Begin();
                MenuCoverImage.ImageSource = bigBmp;

                var miniBmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(GetSquareThumbnail(track.ThumbnailUrl), UriKind.Absolute));
                miniBmp.DecodePixelWidth = 100;
                MiniCoverImage.ImageSource = miniBmp;
            }

            bool isFav = favoriteTracks.Any(t => t.VideoId == track.VideoId);
            BigHeartBtn.Content = isFav ? "♥" : "♡";
            BigHeartBtn.Foreground = isFav ? _greenBrush : _whiteBrush;

            var ignored = UpdateLyricsAsync(track.Title, track.ChannelName);
            UpdateNowPlayingGradient(track.Title, track.ChannelName);
            YTMusicWP.Services.TileService.UpdateNowPlayingWithQueue(track.Title, track.ChannelName, track.ThumbnailUrl, null);

            // BUG FIX: Xác định activeList TRƯỚC khi insert vào history.
            // Nếu detect sau khi insert, historyTracks.Contains(track) sẽ luôn true,
            // gây mất nguồn context thực sự (search, playlist, favorites...).
            ObservableCollection<YouTubeTrack> activeList = homeTracks;
            if (searchResults.Contains(track)) activeList = searchResults;
            else if (favoriteTracks.Contains(track)) activeList = favoriteTracks;
            else if (downloadedTracks.Contains(track)) activeList = downloadedTracks;
            else if (podcastTracks.Contains(track)) activeList = podcastTracks;
            else if (audiobookTracks.Contains(track)) activeList = audiobookTracks;
            else if (historyTracks.Contains(track)) activeList = historyTracks;
            else if (_currentViewingPlaylist != null && _currentViewingPlaylist.Tracks.Contains(track)) activeList = _currentViewingPlaylist.Tracks;
            else if (ArtistSongsList.ItemsSource != null) { var artistList = ArtistSongsList.ItemsSource as ObservableCollection<YouTubeTrack>; if (artistList != null && artistList.Contains(track)) activeList = artistList; }

            // Cập nhật lịch sử SAU khi đã chọn activeList
            var existingHistory = historyTracks.FirstOrDefault(t => t.VideoId == track.VideoId);
            if (existingHistory != null) historyTracks.Remove(existingHistory);
            historyTracks.Insert(0, track);
            if (historyTracks.Count > 20) historyTracks.RemoveAt(historyTracks.Count - 1);
            var ignoredHistory = SaveHistoryAsyncTask();

            // Resolve URL cho bài hiện tại từ foreground (HttpClient mạnh hơn AudioTask)
            string resolvedUrl = "";
            if (!track.VideoId.StartsWith("LOCAL:"))
            {
                try
                {
                    resolvedUrl = await InnerTubeClient.ResolveStreamUrlAsync(track.VideoId, false) ?? "";
                }
                catch { resolvedUrl = ""; }
            }

            if (playRequestGeneration != _playTrackRequestGeneration ||
                playbackSurfaceTransitionGeneration != _videoTransitionGeneration ||
                currentTrack == null || currentTrack.VideoId != track.VideoId)
            {
                return;
            }

            // OPTIMIZATION: Giới hạn mảng gửi sang BackgroundTask để tránh lỗi IPC Payload quá tải (RAM 512MB)
            int maxItems = 100;
            int half = maxItems / 2;
            int sliceStart = Math.Max(0, activeList.IndexOf(track) - half);
            int sliceEnd = Math.Min(activeList.Count - 1, activeList.IndexOf(track) + half);
            int count = sliceEnd - sliceStart + 1;
            int relativeStartIndex = activeList.IndexOf(track) - sliceStart;
            if (relativeStartIndex < 0) relativeStartIndex = 0;

            string[] urls = new string[count];
            string[] titles = new string[count];
            string[] artists = new string[count];
            string[] videoIds = new string[count];
            string[] thumbnails = new string[count];

            currentQueueTracks.Clear();
            for (int i = 0; i < activeList.Count; i++) currentQueueTracks.Add(activeList[i]); // Vẫn giữ full cho UI

            for (int i = 0; i < count; i++)
            {
                var t = activeList[sliceStart + i];
                if (i == relativeStartIndex && !string.IsNullOrEmpty(resolvedUrl))
                    urls[i] = resolvedUrl; 
                else
                    urls[i] = t.VideoId.StartsWith("LOCAL:")
                        ? GetLocalTrackUrl(t.VideoId)
                        : ""; 
                titles[i] = t.Title ?? "";
                artists[i] = t.ChannelName ?? "";
                videoIds[i] = t.VideoId ?? "";
                thumbnails[i] = t.ThumbnailUrl ?? "";
            }

            // Update Live Tile with upcoming queue
            YTMusicWP.Services.TileService.UpdateNowPlayingWithQueue(track.Title, track.ChannelName, track.ThumbnailUrl, activeList.Skip(activeList.IndexOf(track) + 1));

            _handoffUrls = urls;
            _handoffTitles = titles;
            _handoffArtists = artists;
            _handoffVideoIds = videoIds;
            _handoffThumbnails = thumbnails;
            _handoffStartIndex = relativeStartIndex;

            if (!_showVideoSurface && !IsVideoSurfaceModeRequested())
            {
                ForegroundVideo.Visibility = Visibility.Collapsed;
                NowPlayingCoverRect.Visibility = Visibility.Visible;
                VideoSurfaceToggleBtn.Visibility = VideoModeToggle.IsOn && !track.VideoId.StartsWith("LOCAL:")
                    ? Visibility.Visible : Visibility.Collapsed;
                VideoSurfaceToggleBtn.Content = "VIDEO";
                VideoFullscreenBtn.Visibility = Visibility.Collapsed;

                var message = new ValueSet {
                    { "UpdatePlaylist", "" }, { "Urls", urls }, { "Titles", titles }, { "Artists", artists },
                    { "VideoIds", videoIds }, { "Thumbnails", thumbnails }, { "StartIndex", relativeStartIndex }, { "FastUrl", urls[relativeStartIndex] }
                };
                SendPlaylistToBackground(message, playbackCommandGeneration);
            }
        }

        private void SyncQueueToBackground()
        {
            if (currentQueueTracks.Count == 0) return;
            
            // Tìm currentIndex thực sự trong mảng UI
            int actualCurrentIndex = -1;
            if (currentTrack != null) {
                for (int i = 0; i < currentQueueTracks.Count; i++) {
                    if (currentQueueTracks[i].VideoId == currentTrack.VideoId) { actualCurrentIndex = i; break; }
                }
            }
            if (actualCurrentIndex == -1) actualCurrentIndex = 0;

            int maxItems = 100;
            int half = maxItems / 2;
            int sliceStart = Math.Max(0, actualCurrentIndex - half);
            int sliceEnd = Math.Min(currentQueueTracks.Count - 1, actualCurrentIndex + half);
            int count = sliceEnd - sliceStart + 1;
            int relativeStartIndex = actualCurrentIndex - sliceStart;

            string[] urls = new string[count];
            string[] titles = new string[count];
            string[] artists = new string[count];
            string[] videoIds = new string[count];
            string[] thumbnails = new string[count];

            for (int i = 0; i < count; i++)
            {
                var t = currentQueueTracks[sliceStart + i];
                urls[i] = t.VideoId.StartsWith("LOCAL:")
                    ? GetLocalTrackUrl(t.VideoId)
                    : "";
                titles[i] = t.Title ?? "";
                artists[i] = t.ChannelName ?? "";
                videoIds[i] = t.VideoId ?? "";
                thumbnails[i] = t.ThumbnailUrl ?? "";
            }

            var message = new ValueSet {
                { "UpdateQueueOnly", "" }, { "Urls", urls }, { "Titles", titles }, { "Artists", artists },
                { "VideoIds", videoIds }, { "Thumbnails", thumbnails }, { "CurrentIndex", relativeStartIndex }
            };
            try { BackgroundMediaPlayer.SendMessageToBackground(message); }
            catch
            {
                try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "Ping", "" } }); } catch { }
            }
        }

        private async void HeartButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentTrack == null) return;

            // Require login to like songs
            string token = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                ShowToast("Sign in to like songs");
                return;
            }

            var existing = favoriteTracks.FirstOrDefault(t => t.VideoId == currentTrack.VideoId);
            bool isAdding = (existing == null);

            if (existing != null) { favoriteTracks.Remove(existing); BigHeartBtn.Content = "♡"; BigHeartBtn.Foreground = _whiteBrush; }
            else { favoriteTracks.Insert(0, currentTrack); BigHeartBtn.Content = "♥"; BigHeartBtn.Foreground = _greenBrush; }
            SaveFavoritesAsync();

            // Sync to YouTube (skip LOCAL tracks that can't be rated)
            if (!currentTrack.VideoId.StartsWith("LOCAL:"))
            {
                string rating = isAdding ? "like" : "none";
                await RateVideoAsync(currentTrack.VideoId, rating);
            }
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            bool newState = !(settings.Values.ContainsKey("ShuffleMode") ? (bool)settings.Values["ShuffleMode"] : false);
            settings.Values["ShuffleMode"] = newState;
            ShuffleIcon.Foreground = newState ? _greenBrush : _whiteBrush;
            ShuffleDot.Visibility = newState ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            int newMode = (settings.Values.ContainsKey("RepeatMode") ? (int)settings.Values["RepeatMode"] : 0) + 1;
            if (newMode > 2) newMode = 0;
            settings.Values["RepeatMode"] = newMode;
            UpdateRepeatUI(newMode);
        }

        private void UpdateRepeatUI(int mode)
        {
            if (mode == 0) { RepeatIcon.Glyph = "\uE1CD"; RepeatIcon.Foreground = _whiteBrush; RepeatDot.Visibility = Visibility.Collapsed; }
            else if (mode == 1) { RepeatIcon.Glyph = "\uE1CD"; RepeatIcon.Foreground = _greenBrush; RepeatDot.Visibility = Visibility.Visible; }
            else if (mode == 2) { RepeatIcon.Glyph = "\uE1CC"; RepeatIcon.Foreground = _greenBrush; RepeatDot.Visibility = Visibility.Visible; }
        }

        private void SetupTimer()
        {
            try { _appMediaPlayer.CurrentStateChanged += BackgroundMediaPlayer_CurrentStateChanged; } catch { }
            _bgTimer = new Timer(TimerCallback, null, 0, 1000);
        }

        private async void BackgroundMediaPlayer_CurrentStateChanged(MediaPlayer sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        if (_showVideoSurface) return;
                        MediaPlayerState state = sender.CurrentState;
                        bool isPlaying = state == MediaPlayerState.Playing;
                        if (isPlaying)
                        {
                            // Raw MediaPlayer callbacks have no playlist/generation
                            // identity. Do not let an old Playing callback override a
                            // newer Pause or confirm a pending handoff.
                            if (!_imagePlaybackStartPending && _imagePlaybackRequested)
                                _backgroundPositionIsCurrent = true;
                            SetPlayPauseIcon(_imagePlaybackRequested);
                        }
                        else if (state == MediaPlayerState.Opening ||
                            state == MediaPlayerState.Buffering)
                        {
                            // Opening/Buffering is not Paused. Preserve the latest user
                            // intent so the visible button and the command sent on tap agree.
                            SetPlayPauseIcon(_imagePlaybackRequested);
                        }
                        else if (state == MediaPlayerState.Paused)
                        {
                            // SetUriSource can publish an old Paused callback while the
                            // handoff playlist or Resume is still opening.
                            if (_imagePlaybackStartPending && _imagePlaybackRequested)
                                SetPlayPauseIcon(true);
                            else
                            {
                                _imagePlaybackRequested = false;
                                SetPlayPauseIcon(false);
                            }
                        }
                        else if (state == MediaPlayerState.Stopped ||
                            state == MediaPlayerState.Closed)
                        {
                            _backgroundPositionIsCurrent = false;
                            if (_imagePlaybackStartPending && _imagePlaybackRequested)
                                SetPlayPauseIcon(true);
                            else
                            {
                                _imagePlaybackRequested = false;
                                SetPlayPauseIcon(false);
                            }
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private async void TimerCallback(object state)
        {
            // [OPT-P1] Bail-out sớm TRƯỚC khi gọi Dispatcher — tiết kiệm thread switch trên WP8.1
            if (_isSliderManipulating) return;
            int transitionGeneration = _videoTransitionGeneration;
            string expectedVideoId = currentTrack != null ? currentTrack.VideoId : "";
            bool videoMode = SafeGetBool(ApplicationData.Current.LocalSettings.Values, "VideoMode", true) && _showVideoSurface;
            if (videoMode)
            {
                try
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (transitionGeneration != _videoTransitionGeneration ||
                            !_showVideoSurface || currentTrack == null ||
                            currentTrack.VideoId != expectedVideoId)
                            return;
                        MediaElement videoPlayer = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                        if (videoPlayer.Source == null) return;
                        TimeSpan videoDuration = videoPlayer.NaturalDuration.HasTimeSpan
                            ? videoPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero;
                        if (videoDuration.TotalSeconds <= 0) return;
                        TimeSpan videoPosition = videoPlayer.Position;
                        MusicSlider.Maximum = videoDuration.TotalSeconds;
                        MusicSlider.Value = videoPosition.TotalSeconds;
                        CurrentTimeText.Text = FormatPlaybackTime(videoPosition);
                        TotalTimeText.Text = FormatPlaybackTime(videoDuration);
                        MiniProgressBar.Maximum = videoDuration.TotalSeconds;
                        MiniProgressBar.Value = videoPosition.TotalSeconds;
                        if (_isFullscreenVideo && !_isFullscreenSliderManipulating)
                        {
                            FullscreenVideoSlider.Maximum = videoDuration.TotalSeconds;
                            FullscreenVideoSlider.Value = videoPosition.TotalSeconds;
                            FullscreenCurrentTimeText.Text = FormatPlaybackTime(videoPosition);
                            FullscreenTotalTimeText.Text = FormatPlaybackTime(videoDuration);
                        }
                    });
                }
                catch { }
                return;
            }
            MediaPlayer session;
            try { session = _appMediaPlayer; } catch { return; }
            if (session == null || !_backgroundPositionIsCurrent ||
                session.CurrentState != MediaPlayerState.Playing) return;

            TimeSpan pos, dur;
            try { pos = session.Position; dur = session.NaturalDuration; } catch { return; }
            if (dur.TotalSeconds <= 0) return;

            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    try
                    {
                        if (transitionGeneration != _videoTransitionGeneration ||
                            _showVideoSurface || !_backgroundPositionIsCurrent ||
                            !object.ReferenceEquals(session, _appMediaPlayer) ||
                            session.CurrentState != MediaPlayerState.Playing ||
                            currentTrack == null || currentTrack.VideoId != expectedVideoId)
                            return;
                        MusicSlider.Maximum    = dur.TotalSeconds;
                        MusicSlider.Value      = pos.TotalSeconds;
                        CurrentTimeText.Text   = FormatPlaybackTime(pos);
                        TotalTimeText.Text     = FormatPlaybackTime(dur);
                        MiniProgressBar.Maximum = dur.TotalSeconds;
                        MiniProgressBar.Value   = pos.TotalSeconds;

                        if (NowPlayingView.Visibility != Visibility.Visible && FullscreenLyricsView.Visibility != Visibility.Visible) return;
                        bool isFullscreen = FullscreenLyricsView.Visibility == Visibility.Visible;
                        if (!isFullscreen && NowPlayingPivot.SelectedIndex != 1) return;
                        if (currentLyrics.Count == 0 || (!isFullscreen && LyricsListView.Visibility != Visibility.Visible)) return;

                        int newIndex = -1;
                        for (int i = 0; i < currentLyrics.Count; i++)
                        {
                            if (pos >= currentLyrics[i].Time.Subtract(TimeSpan.FromSeconds(0.2))) newIndex = i;
                            else break;
                        }

                        if (newIndex == currentLyricIndex || newIndex < 0) return;

                        int oldIndex = currentLyricIndex;
                        currentLyricIndex = newIndex;

                        // Target ListView = fullscreen or regular
                        var targetListView = isFullscreen ? FullscreenLyricsListView : LyricsListView;

                        // Animate OLD lyric
                        if (oldIndex >= 0 && oldIndex < currentLyrics.Count)
                        {
                            currentLyrics[oldIndex].ColorBrush = _lyricInactiveBrush;

                            if (!isFullscreen)
                            {
                                var oldContainer = targetListView.ContainerFromIndex(oldIndex) as FrameworkElement;
                                if (oldContainer != null)
                                {
                                    var oldScale = oldContainer.RenderTransform as Windows.UI.Xaml.Media.ScaleTransform;
                                    if (oldScale == null)
                                    {
                                        oldScale = new Windows.UI.Xaml.Media.ScaleTransform { ScaleX = 1, ScaleY = 1 };
                                        oldContainer.RenderTransformOrigin = new Point(0, 0.5);
                                        oldContainer.RenderTransform = oldScale;
                                    }
                                    // [OPT-1] Reuse cached easing + single storyboard
                                    AnimateLyricOut(oldContainer, oldScale);
                                }
                                else { currentLyrics[oldIndex].Opacity = 0.35; }
                            }
                            else
                            {
                                var oldContainer = targetListView.ContainerFromIndex(oldIndex) as FrameworkElement;
                                if (oldContainer != null) AnimateOpacity(oldContainer, 0.5);
                            }
                        }

                        // Animate NEW lyric
                        currentLyrics[currentLyricIndex].ColorBrush = _lyricActiveBrush;

                        if (isFullscreen)
                        {
                            var fsContainer = targetListView.ContainerFromIndex(currentLyricIndex) as FrameworkElement;
                            if (fsContainer != null) AnimateOpacity(fsContainer, 1.0);
                        }
                        else
                        {
                            var newContainer = targetListView.ContainerFromIndex(currentLyricIndex) as FrameworkElement;
                            if (newContainer != null)
                            {
                                var scaleTransform = newContainer.RenderTransform as Windows.UI.Xaml.Media.ScaleTransform;
                                if (scaleTransform == null)
                                {
                                    scaleTransform = new Windows.UI.Xaml.Media.ScaleTransform { ScaleX = 0.85, ScaleY = 0.85 };
                                    newContainer.RenderTransformOrigin = new Point(0, 0.5);
                                    newContainer.RenderTransform = scaleTransform;
                                }
                                // [OPT-1] Reuse cached easing + single storyboard
                                AnimateLyricIn(newContainer, scaleTransform);
                            }
                            else { currentLyrics[currentLyricIndex].Opacity = 1.0; }
                        }

                        // Smooth center-scroll (replaces abrupt ScrollIntoView)
                        ScrollViewer scrollViewer;
                        if (isFullscreen)
                        {
                            if (_cachedFullscreenLyricsScrollViewer == null)
                                _cachedFullscreenLyricsScrollViewer = GetScrollViewer(FullscreenLyricsListView);
                            scrollViewer = _cachedFullscreenLyricsScrollViewer;
                        }
                        else
                        {
                            if (_cachedLyricsScrollViewer == null)
                                _cachedLyricsScrollViewer = GetScrollViewer(LyricsListView);
                            scrollViewer = _cachedLyricsScrollViewer;
                        }

                        var activeContainer = targetListView.ContainerFromIndex(currentLyricIndex) as FrameworkElement;
                        if (activeContainer == null)
                        {
                            // Item is virtualized out of view because user seeked far away.
                            targetListView.ScrollIntoView(currentLyrics[currentLyricIndex]);
                            targetListView.UpdateLayout();
                            activeContainer = targetListView.ContainerFromIndex(currentLyricIndex) as FrameworkElement;
                        }
                        
                        if (scrollViewer != null && activeContainer != null)
                        {
                            var transform    = activeContainer.TransformToVisual(scrollViewer);
                            var lyricPos     = transform.TransformPoint(new Point(0, 0));
                            double targetOff = scrollViewer.VerticalOffset + lyricPos.Y
                                            - (scrollViewer.ViewportHeight / 2.0)
                                            + (activeContainer.ActualHeight / 2.0);
                            scrollViewer.ChangeView(null, targetOff, null, false);
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void MusicSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _isSliderManipulating = true;
        private void MusicSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isSliderManipulating = false;
            try
            {
                MediaElement videoPlayer = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                if (_showVideoSurface && videoPlayer.Source != null && videoPlayer.NaturalDuration.HasTimeSpan)
                {
                    videoPlayer.Position = TimeSpan.FromSeconds(Math.Min(MusicSlider.Value,
                        Math.Max(0, videoPlayer.NaturalDuration.TimeSpan.TotalSeconds - 2)));
                    videoPlayer.Play();
                    return;
                }
                if (_appMediaPlayer.CurrentState != MediaPlayerState.Closed)
                {
                    // FIX #7: Clamp giá trị seek để không bị âm với clip ngắn
                    _appMediaPlayer.Position = TimeSpan.FromSeconds(Math.Min(MusicSlider.Value, Math.Max(0, _appMediaPlayer.NaturalDuration.TotalSeconds - 2)));
                    if (_appMediaPlayer.CurrentState == MediaPlayerState.Paused)
                    {
                        _imagePlaybackRequested = true;
                        SendImageTransportCommand("ResumePlaybackMessage");
                    }
                }
            }
            catch { }
        }

        private MediaElement GetActiveVideoPlayer()
        {
            if (_isFullscreenVideo && FullscreenVideoView.Visibility == Visibility.Visible &&
                FullscreenVideo.Source != null)
            {
                return FullscreenVideo;
            }
            return ForegroundVideo;
        }

        private static bool IsVideoPlaybackActive(MediaElement player)
        {
            return player != null &&
                (player.CurrentState == MediaElementState.Playing ||
                 player.CurrentState == MediaElementState.Buffering ||
                 player.CurrentState == MediaElementState.Opening);
        }

        private bool IsVideoSurfaceModeRequested()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                return values.ContainsKey("PlaybackSurfaceMode") &&
                    values["PlaybackSurfaceMode"] != null &&
                    values["PlaybackSurfaceMode"].ToString() == "Video";
            }
            catch
            {
                return _showVideoSurface;
            }
        }

        private TimeSpan GetPositionForVideoSwitch()
        {
            TimeSpan fallback = _videoPositionBeforeSuspend;
            try
            {
                if (ForegroundVideo.Source != null && ForegroundVideo.Position > fallback)
                    fallback = ForegroundVideo.Position;
            }
            catch { }

            try
            {
                if (_pendingBackgroundPlaylist != null) return fallback;
                if (_appMediaPlayer == null || currentTrack == null) return fallback;
                var values = ApplicationData.Current.LocalSettings.Values;
                string backgroundVideoId = values.ContainsKey("CurrentVideoId")
                    ? values["CurrentVideoId"].ToString() : "";
                MediaPlayerState state = _appMediaPlayer.CurrentState;
                if (backgroundVideoId == currentTrack.VideoId &&
                    _backgroundPositionIsCurrent &&
                    (state == MediaPlayerState.Playing || state == MediaPlayerState.Paused))
                {
                    return _appMediaPlayer.Position;
                }
            }
            catch { }
            return fallback;
        }

        private long SendForegroundVideoMode(bool active, bool isPlaying)
        {
            return SendForegroundVideoMode(active, isPlaying, NextPlaybackCommandGeneration());
        }

        private long SendForegroundVideoMode(bool active, bool isPlaying, long commandGeneration)
        {
            return SendForegroundVideoMode(active, isPlaying, commandGeneration, false);
        }

        private long SendForegroundVideoMode(bool active, bool isPlaying,
            long commandGeneration, bool playlistWillFollow)
        {
            if (commandGeneration > _lastPlaybackCommandGeneration)
                _lastPlaybackCommandGeneration = commandGeneration;
            if (active && commandGeneration >= _lastPlaybackCommandGeneration)
                _pendingBackgroundTransportCommand = null;
            try
            {
                var message = new ValueSet {
                    { "SetForegroundVideoMode", active },
                    { "VideoIsPlaying", isPlaying },
                    { "PlaybackCommandGeneration", commandGeneration }
                };
                if (playlistWillFollow) message.Add("PlaylistWillFollow", true);
                _pendingForegroundVideoModeCommand = message;
                RetryPendingVideoModeCommand(message);
                BackgroundMediaPlayer.SendMessageToBackground(message);
            }
            catch { }
            return commandGeneration;
        }

        private async void RetryPendingVideoModeCommand(ValueSet message)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await System.Threading.Tasks.Task.Delay(400);
                if (!object.ReferenceEquals(_pendingForegroundVideoModeCommand,
                    message))
                    return;
                try { BackgroundMediaPlayer.SendMessageToBackground(message); }
                catch { }
            }
        }

        private void SendImageTransportCommand(string commandKey)
        {
            bool startsPlayback = commandKey == "ResumePlaybackMessage" ||
                commandKey == "NextTrackMessage" ||
                commandKey == "PrevTrackMessage" ||
                commandKey == "RestartTrackMessage";
            if (startsPlayback)
            {
                _imagePlaybackStartPending = true;
                _backgroundPositionIsCurrent = false;
            }
            else if (commandKey == "PausePlaybackMessage" ||
                commandKey == "StopPlaybackMessage")
            {
                _imagePlaybackStartPending = false;
            }
            if (commandKey == "NextTrackMessage" ||
                commandKey == "PrevTrackMessage" ||
                commandKey == "RestartTrackMessage")
            {
                _backgroundPositionIsCurrent = false;
            }
            long commandGeneration = NextPlaybackCommandGeneration();
            var message = new ValueSet {
                { commandKey, "" },
                { "PlaybackCommandGeneration", commandGeneration }
            };
            _pendingBackgroundTransportCommand = message;
            try
            {
                BackgroundMediaPlayer.MessageReceivedFromBackground -= BackgroundMediaPlayer_MessageReceivedFromBackground;
                BackgroundMediaPlayer.MessageReceivedFromBackground += BackgroundMediaPlayer_MessageReceivedFromBackground;
                if (_appMediaPlayer == null || _appMediaPlayer.CurrentState == MediaPlayerState.Closed)
                {
                    _appMediaPlayer = BackgroundMediaPlayer.Current;
                    _appMediaPlayer.CurrentStateChanged -= BackgroundMediaPlayer_CurrentStateChanged;
                    _appMediaPlayer.CurrentStateChanged += BackgroundMediaPlayer_CurrentStateChanged;
                }
                if (_pendingBackgroundPlaylist == null)
                    BackgroundMediaPlayer.SendMessageToBackground(message);
                else
                    BackgroundMediaPlayer.SendMessageToBackground(
                        new ValueSet { { "Ping", "" } });
            }
            catch
            {
                try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "Ping", "" } }); } catch { }
            }
            RetryPendingTransportCommand(message);
        }

        private async void RetryPendingTransportCommand(ValueSet message)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await System.Threading.Tasks.Task.Delay(400);
                if (!object.ReferenceEquals(_pendingBackgroundTransportCommand, message))
                    return;
                try
                {
                    if (_pendingBackgroundPlaylist == null)
                        BackgroundMediaPlayer.SendMessageToBackground(message);
                    BackgroundMediaPlayer.SendMessageToBackground(
                        new ValueSet { { "Ping", "" } });
                }
                catch { }
            }
        }

        private void SendVideoPlaybackState(bool isPlaying)
        {
            try
            {
                BackgroundMediaPlayer.SendMessageToBackground(
                    new ValueSet { { "VideoPlaybackState", isPlaying } });
            }
            catch { }
        }

        private void HandleVideoTransportCommand(string command)
        {
            if (!_showVideoSurface)
            {
                if (command == "Stop")
                {
                    StopPlaybackButton_Click(null, null);
                }
                else if (command == "Next")
                {
                    NextButton_Click(null, null);
                }
                else if (command == "Previous")
                {
                    PrevButton_Click(null, null);
                }
                else if (command == "Pause")
                {
                    _imagePlaybackRequested = false;
                    SendImageTransportCommand("PausePlaybackMessage");
                    SetPlayPauseIcon(false);
                }
                else if (command == "Play")
                {
                    _imagePlaybackRequested = true;
                    SendImageTransportCommand("ResumePlaybackMessage");
                    SetPlayPauseIcon(true);
                }
                return;
            }
            if (command == "Stop")
            {
                StopPlaybackButton_Click(null, null);
                return;
            }

            MediaElement videoPlayer = GetActiveVideoPlayer();
            if (videoPlayer == null || videoPlayer.Source == null) return;

            if (command == "Next")
            {
                PlayAdjacentVideo(1);
                return;
            }
            if (command == "Previous")
            {
                if (videoPlayer.Position.TotalSeconds > 3)
                {
                    videoPlayer.Position = TimeSpan.Zero;
                    videoPlayer.Play();
                    SetPlayPauseIcon(true);
                    SendVideoPlaybackState(true);
                }
                else
                {
                    PlayAdjacentVideo(-1);
                }
                return;
            }

            bool shouldPause = command == "Pause" ||
                (command == "Toggle" && IsVideoPlaybackActive(videoPlayer));
            if (shouldPause)
            {
                videoPlayer.Pause();
                SetPlayPauseIcon(false);
                SendVideoPlaybackState(false);
            }
            else if (command == "Play" || command == "Toggle")
            {
                videoPlayer.Play();
                SetPlayPauseIcon(true);
                SendVideoPlaybackState(true);
            }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_showVideoSurface && GetActiveVideoPlayer().Source != null)
                {
                    HandleVideoTransportCommand("Toggle");
                    return;
                }

                MediaPlayerState state = _appMediaPlayer != null
                    ? _appMediaPlayer.CurrentState : MediaPlayerState.Closed;
                // Opening/Buffering only describes the media engine, not the latest
                // requested action. Treating it as Playing made a visible Resume tap
                // send Pause during VIDEO -> IMAGE handoff.
                bool shouldPause = _imagePlaybackRequested ||
                    state == MediaPlayerState.Playing;
                if (shouldPause)
                {
                    _imagePlaybackRequested = false;
                    SendImageTransportCommand("PausePlaybackMessage");
                    SetPlayPauseIcon(false);
                }
                else
                {
                    _imagePlaybackRequested = true;
                    if (state == MediaPlayerState.Closed &&
                        _pendingBackgroundPlaylist == null && currentTrack != null)
                        PlayTrack(currentTrack);
                    else
                        SendImageTransportCommand("ResumePlaybackMessage");
                    SetPlayPauseIcon(true);
                }
            }
            catch { }
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_showVideoSurface && GetActiveVideoPlayer().Source != null)
                {
                    HandleVideoTransportCommand("Previous");
                    return;
                }
                bool restartCurrent = false;
                try
                {
                    restartCurrent = _appMediaPlayer != null &&
                        (_appMediaPlayer.CurrentState == MediaPlayerState.Playing ||
                         _appMediaPlayer.CurrentState == MediaPlayerState.Paused) &&
                        _appMediaPlayer.Position.TotalSeconds > 3;
                }
                catch { }
                _imagePlaybackRequested = true;
                SendImageTransportCommand(restartCurrent
                    ? "RestartTrackMessage" : "PrevTrackMessage");
                SetPlayPauseIcon(true);
            }
            catch { SendImageTransportCommand("PrevTrackMessage"); }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_showVideoSurface && GetActiveVideoPlayer().Source != null)
                {
                    HandleVideoTransportCommand("Next");
                    return;
                }
                _imagePlaybackRequested = true;
                SendImageTransportCommand("NextTrackMessage");
                SetPlayPauseIcon(true);
            }
            catch
            {
                SendImageTransportCommand("NextTrackMessage");
            }
        }

        private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
            _playTrackRequestGeneration++;
            _videoTransitionGeneration++;
            _showVideoSurface = false;
            _imagePlaybackRequested = false;
            _imagePlaybackStartPending = false;
            _backgroundPositionIsCurrent = false;
            _pendingBackgroundPlaylist = null;
            try { ApplicationData.Current.LocalSettings.Values["PlaybackSurfaceMode"] = "Image"; } catch { }
            SendForegroundVideoMode(false, false);
            try
            {
                if (_isFullscreenVideo) CloseFullscreenVideo_Click(null, null);
                ForegroundVideo.Stop();
                ForegroundVideo.Source = null;
                FullscreenVideo.Stop();
                FullscreenVideo.Source = null;
                SendImageTransportCommand("StopPlaybackMessage");
                _appMediaPlayer = null;
            }
            catch { }

            currentTrack = null;
            currentQueueTracks.Clear();
            MusicSlider.Value = 0;
            MiniProgressBar.Value = 0;
            CurrentTimeText.Text = "0:00";
            TotalTimeText.Text = "0:00";
            NowPlayingView.Visibility = Visibility.Collapsed;
            FullscreenVideoView.Visibility = Visibility.Collapsed;
            MiniPlayerContainer.Visibility = Visibility.Collapsed;
            SetPlayPauseIcon(false);
        }

        private void ForegroundVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            PlayAdjacentVideo(1);
        }

        private void ForegroundVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowToast("Video playback failed");
            SetPlayPauseIcon(false);
            SendVideoPlaybackState(false);
        }

        private void PlayAdjacentVideo(int offset)
        {
            if (currentTrack == null || currentQueueTracks.Count == 0) return;
            int index = -1;
            for (int i = 0; i < currentQueueTracks.Count; i++)
            {
                if (currentQueueTracks[i].VideoId == currentTrack.VideoId)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) index = 0;
            int next = index + offset;
            if (next >= 0 && next < currentQueueTracks.Count) PlayTrack(currentQueueTracks[next]);
            else SetPlayPauseIcon(false);
        }

        private bool _showVideoSurface = false;
        private bool _isFullscreenVideo = false;
        private bool _fullscreenLandscape = false;
        private bool _isFullscreenSliderManipulating = false;
        private TimeSpan _fullscreenSeekPosition = TimeSpan.Zero;
        private string[] _handoffUrls;
        private string[] _handoffTitles;
        private string[] _handoffArtists;
        private string[] _handoffVideoIds;
        private string[] _handoffThumbnails;
        private int _handoffStartIndex;
        private TimeSpan _videoPositionBeforeSuspend = TimeSpan.Zero;
        private bool _videoWasPlayingBeforeSuspend;
        private TimeSpan _pendingVideoStartPosition = TimeSpan.Zero;

        private void HandoffVideoToBackground()
        {
            if (currentTrack == null) return;
            _videoTransitionGeneration++;
            YouTubeTrack handoffTrack = currentTrack;
            string handoffVideoId = handoffTrack.VideoId ?? "";
            MediaElement player = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;

            TimeSpan handoffPosition = TimeSpan.Zero;
            try { handoffPosition = player.Position; } catch { }
            if (handoffPosition <= TimeSpan.Zero)
                handoffPosition = _pendingVideoStartPosition;
            if (handoffPosition <= TimeSpan.Zero)
                handoffPosition = _videoPositionBeforeSuspend;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (handoffPosition <= TimeSpan.Zero && values.ContainsKey("VideoPositionTicks"))
                    handoffPosition = TimeSpan.FromTicks((long)values["VideoPositionTicks"]);
            }
            catch { }

            _videoPositionBeforeSuspend = handoffPosition;
            _imagePlaybackRequested = true;
            _imagePlaybackStartPending = true;
            _backgroundPositionIsCurrent = false;
            _showVideoSurface = false;
            try { ApplicationData.Current.LocalSettings.Values["PlaybackSurfaceMode"] = "Image"; } catch { }
            try { if (player.Source != null) player.Pause(); } catch { }

            ForegroundVideo.Visibility = Visibility.Collapsed;
            NowPlayingCoverRect.Visibility = Visibility.Visible;
            VideoSurfaceToggleBtn.Content = "VIDEO";
            VideoFullscreenBtn.Visibility = Visibility.Collapsed;
            _videoWasPlayingBeforeSuspend = false;
            SetPlayPauseIcon(true);

            bool validQueue = _handoffUrls != null && _handoffUrls.Length > 0 &&
                _handoffTitles != null && _handoffTitles.Length == _handoffUrls.Length &&
                _handoffArtists != null && _handoffArtists.Length == _handoffUrls.Length &&
                _handoffVideoIds != null && _handoffVideoIds.Length == _handoffUrls.Length &&
                _handoffThumbnails != null && _handoffThumbnails.Length == _handoffUrls.Length;
            int currentIndex = validQueue ? Array.IndexOf(_handoffVideoIds, handoffVideoId) : -1;
            string fastUrl = currentIndex >= 0 ? (_handoffUrls[currentIndex] ?? "") : "";

            if (currentIndex < 0)
            {
                _handoffUrls = new[] { fastUrl };
                _handoffTitles = new[] { handoffTrack.Title ?? "" };
                _handoffArtists = new[] { handoffTrack.ChannelName ?? "" };
                _handoffVideoIds = new[] { handoffVideoId };
                _handoffThumbnails = new[] { handoffTrack.ThumbnailUrl ?? "" };
                _handoffStartIndex = 0;
            }
            else
            {
                _handoffStartIndex = currentIndex;
                if (!string.IsNullOrEmpty(fastUrl)) _handoffUrls[_handoffStartIndex] = fastUrl;
                _handoffTitles[_handoffStartIndex] = handoffTrack.Title ?? "";
                _handoffArtists[_handoffStartIndex] = handoffTrack.ChannelName ?? "";
                _handoffVideoIds[_handoffStartIndex] = handoffVideoId;
                _handoffThumbnails[_handoffStartIndex] = handoffTrack.ThumbnailUrl ?? "";
            }

            var message = new ValueSet {
                { "UpdatePlaylist", "" }, { "Urls", _handoffUrls }, { "Titles", _handoffTitles },
                { "Artists", _handoffArtists }, { "VideoIds", _handoffVideoIds },
                { "Thumbnails", _handoffThumbnails }, { "StartIndex", _handoffStartIndex },
                { "FastUrl", fastUrl }, { "StartPositionTicks", handoffPosition.Ticks }
            };
            SendPlaylistToBackground(message);
        }

        private void PauseVideoForSuspension()
        {
            try
            {
                if (!_showVideoSurface) return;
                MediaElement player = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                _videoWasPlayingBeforeSuspend = player.CurrentState == MediaElementState.Playing ||
                    player.CurrentState == MediaElementState.Buffering ||
                    player.CurrentState == MediaElementState.Opening;
                _videoPositionBeforeSuspend = player.Position;
                if (_videoWasPlayingBeforeSuspend) player.Pause();
                SendVideoPlaybackState(false);
                var values = ApplicationData.Current.LocalSettings.Values;
                values["VideoWasPlayingBeforeSuspend"] = _videoWasPlayingBeforeSuspend;
                values["VideoPositionTicks"] = _videoPositionBeforeSuspend.Ticks;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task ResumeSuspendedVideoAsync()
        {
            if (!_showVideoSurface) return;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!_videoWasPlayingBeforeSuspend && values.ContainsKey("VideoWasPlayingBeforeSuspend"))
                    _videoWasPlayingBeforeSuspend = (bool)values["VideoWasPlayingBeforeSuspend"];
                if (_videoPositionBeforeSuspend == TimeSpan.Zero && values.ContainsKey("VideoPositionTicks"))
                    _videoPositionBeforeSuspend = TimeSpan.FromTicks((long)values["VideoPositionTicks"]);
                if (!_videoWasPlayingBeforeSuspend) return;

                _pendingBackgroundPlaylist = null;
                SendForegroundVideoMode(true, true);
                MediaElement player = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                if (player.Source == null && currentTrack != null && !currentTrack.VideoId.StartsWith("LOCAL:"))
                {
                    string videoUrl = await InnerTubeClient.ResolveStreamUrlAsync(currentTrack.VideoId, true) ?? "";
                    if (!_showVideoSurface || string.IsNullOrEmpty(videoUrl))
                    {
                        SendVideoPlaybackState(false);
                        return;
                    }
                    _pendingVideoStartPosition = _videoPositionBeforeSuspend;
                    player.Source = new Uri(videoUrl, UriKind.Absolute);
                    _videoWasPlayingBeforeSuspend = false;
                    try { ApplicationData.Current.LocalSettings.Values["VideoWasPlayingBeforeSuspend"] = false; } catch { }
                    return;
                }
                if (player.Source != null)
                {
                    player.Position = _videoPositionBeforeSuspend;
                    player.Play();
                    SetPlayPauseIcon(true);
                    SendVideoPlaybackState(true);
                }
            }
            catch { }
            _videoWasPlayingBeforeSuspend = false;
            try { ApplicationData.Current.LocalSettings.Values["VideoWasPlayingBeforeSuspend"] = false; } catch { }
        }

        private static string FormatPlaybackTime(TimeSpan value)
        {
            return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
        }

        private async void VideoSurfaceToggle_Click(object sender, RoutedEventArgs e)
        {
            if (currentTrack == null || currentTrack.VideoId.StartsWith("LOCAL:")) return;

            if (_showVideoSurface)
            {
                HandoffVideoToBackground();
                return;
            }

            if (!VideoModeToggle.IsOn) return;

            int videoTransitionGeneration = ++_videoTransitionGeneration;
            string requestedVideoId = currentTrack.VideoId;
            TimeSpan position = GetPositionForVideoSwitch();
            string videoUrl = null;
            if (ForegroundVideo.Source == null)
            {
                videoUrl = "";
                try { videoUrl = await InnerTubeClient.ResolveStreamUrlAsync(currentTrack.VideoId, true) ?? ""; }
                catch { }
                if (videoTransitionGeneration != _videoTransitionGeneration ||
                    currentTrack == null || currentTrack.VideoId != requestedVideoId ||
                    !VideoModeToggle.IsOn)
                {
                    return;
                }
                if (string.IsNullOrEmpty(videoUrl)) { ShowToast("Unable to load video"); return; }
            }

            _showVideoSurface = true;
            _imagePlaybackRequested = false;
            _imagePlaybackStartPending = false;
            ApplicationData.Current.LocalSettings.Values["PlaybackSurfaceMode"] = "Video";
            _pendingBackgroundPlaylist = null;
            SendForegroundVideoMode(true, true);

            if (ForegroundVideo.Source == null)
            {
                try
                {
                    _pendingVideoStartPosition = position;
                    ForegroundVideo.Source = new Uri(videoUrl, UriKind.Absolute);
                }
                catch
                {
                    _showVideoSurface = false;
                    ApplicationData.Current.LocalSettings.Values["PlaybackSurfaceMode"] = "Image";
                    _imagePlaybackRequested = true;
                    SendImageTransportCommand("ResumePlaybackMessage");
                    ShowToast("Unable to load video");
                    return;
                }
            }
            else
            {
                try
                {
                    ForegroundVideo.Position = position;
                    ForegroundVideo.Play();
                    SendVideoPlaybackState(true);
                }
                catch { }
            }

            ForegroundVideo.Visibility = Visibility.Visible;
            NowPlayingCoverRect.Visibility = Visibility.Collapsed;
            VideoSurfaceToggleBtn.Content = "IMAGE";
            VideoFullscreenBtn.Visibility = Visibility.Visible;
        }

        private void ForegroundVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (!_showVideoSurface) return;
            try
            {
                VideoFullscreenBtn.Visibility = Visibility.Visible;
                ForegroundVideo.Position = _pendingVideoStartPosition;
                _pendingVideoStartPosition = TimeSpan.Zero;
                ForegroundVideo.Play();
                SetPlayPauseIcon(true);
                SendVideoPlaybackState(true);
            }
            catch { }
        }

        private void SavePlaybackUiState()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                values["RestoreNowPlaying"] = NowPlayingView.Visibility == Visibility.Visible;
                values["RestoreImageMode"] = !_showVideoSurface;
                values["PlaybackSurfaceMode"] = _showVideoSurface ? "Video" : "Image";
            }
            catch { }
        }

        private void RestorePlaybackUiState()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                bool restoreNowPlaying = values.ContainsKey("RestoreNowPlaying") && (bool)values["RestoreNowPlaying"];
                bool restoreImageMode = values.ContainsKey("PlaybackSurfaceMode")
                    ? values["PlaybackSurfaceMode"].ToString() != "Video"
                    : values.ContainsKey("RestoreImageMode") && (bool)values["RestoreImageMode"];
                _showVideoSurface = !restoreImageMode;

                if (currentTrack != null && VideoModeToggle.IsOn && !currentTrack.VideoId.StartsWith("LOCAL:"))
                {
                    VideoSurfaceToggleBtn.Visibility = Visibility.Visible;
                    VideoFullscreenBtn.Visibility = ForegroundVideo.Source != null ? Visibility.Visible : Visibility.Collapsed;
                    VideoSurfaceToggleBtn.Content = _showVideoSurface ? "IMAGE" : "VIDEO";
                    if (restoreImageMode)
                    {
                        ForegroundVideo.Visibility = Visibility.Collapsed;
                        NowPlayingCoverRect.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        ForegroundVideo.Visibility = Visibility.Visible;
                        NowPlayingCoverRect.Visibility = Visibility.Collapsed;
                    }
                }

                if (restoreNowPlaying && currentTrack != null)
                {
                    NowPlayingView.Visibility = Visibility.Visible;
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => StartTitleMarquee());
                }
            }
            catch { }
        }

        private void OpenFullscreenVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!VideoModeToggle.IsOn || ForegroundVideo.Source == null) return;
            _isFullscreenVideo = true;
            _fullscreenSeekPosition = ForegroundVideo.Position;
            ForegroundVideo.Pause();
            FullscreenVideoControls.Visibility = Visibility.Visible;
            FullscreenVideoView.Visibility = Visibility.Visible;
            SetFullscreenSystemUi(true);
            FullscreenVideo.Source = ForegroundVideo.Source;
        }

        private async void SetFullscreenSystemUi(bool fullscreen)
        {
            try
            {
                var statusBar = Windows.UI.ViewManagement.StatusBar.GetForCurrentView();
                if (fullscreen) await statusBar.HideAsync();
                else await statusBar.ShowAsync();
            }
            catch { }
        }

        private void FullscreenVideoView_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!_isFullscreenVideo) return;
            FullscreenVideoControls.Visibility = FullscreenVideoControls.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void FullscreenVideoControls_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void FullscreenVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (_fullscreenSeekPosition > TimeSpan.Zero)
            {
                FullscreenVideo.Position = _fullscreenSeekPosition;
                _fullscreenSeekPosition = TimeSpan.Zero;
            }
            if (FullscreenVideo.NaturalDuration.HasTimeSpan)
            {
                TimeSpan duration = FullscreenVideo.NaturalDuration.TimeSpan;
                FullscreenVideoSlider.Maximum = duration.TotalSeconds;
                FullscreenVideoSlider.Value = FullscreenVideo.Position.TotalSeconds;
                FullscreenCurrentTimeText.Text = FormatPlaybackTime(FullscreenVideo.Position);
                FullscreenTotalTimeText.Text = FormatPlaybackTime(duration);
            }
            FullscreenVideo.Play();
        }

        private void FullscreenVideoSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isFullscreenSliderManipulating = true;
        }

        private void FullscreenVideoSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isFullscreenSliderManipulating = false;
            try
            {
                if (!FullscreenVideo.NaturalDuration.HasTimeSpan) return;
                FullscreenVideo.Position = TimeSpan.FromSeconds(Math.Min(FullscreenVideoSlider.Value,
                    Math.Max(0, FullscreenVideo.NaturalDuration.TimeSpan.TotalSeconds - 2)));
                FullscreenCurrentTimeText.Text = FormatPlaybackTime(FullscreenVideo.Position);
            }
            catch { }
        }

        private void VideoPlayer_CurrentStateChanged(object sender, RoutedEventArgs e)
        {
            var player = sender as MediaElement;
            if (player == null || !_showVideoSurface) return;
            if ((_isFullscreenVideo && player != FullscreenVideo) || (!_isFullscreenVideo && player != ForegroundVideo)) return;
            if (IsVideoPlaybackActive(player))
            {
                SetPlayPauseIcon(true);
                SendVideoPlaybackState(true);
            }
            else if (player.CurrentState == MediaElementState.Paused || player.CurrentState == MediaElementState.Stopped)
            {
                SetPlayPauseIcon(false);
                SendVideoPlaybackState(false);
            }
        }

        private void RotateFullscreenVideo_Click(object sender, RoutedEventArgs e)
        {
            _fullscreenLandscape = !_fullscreenLandscape;
            Windows.Graphics.Display.DisplayInformation.AutoRotationPreferences = _fullscreenLandscape
                ? Windows.Graphics.Display.DisplayOrientations.Landscape
                : Windows.Graphics.Display.DisplayOrientations.Portrait;
        }

        private void CloseFullscreenVideo_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFullscreenVideo) return;
            TimeSpan position = FullscreenVideo.Position;
            FullscreenVideo.Stop();
            FullscreenVideo.Source = null;
            FullscreenVideoView.Visibility = Visibility.Collapsed;
            SetFullscreenSystemUi(false);
            _isFullscreenVideo = false;
            _fullscreenLandscape = false;
            Windows.Graphics.Display.DisplayInformation.AutoRotationPreferences = Windows.Graphics.Display.DisplayOrientations.Portrait;
            ForegroundVideo.Position = position;
            ForegroundVideo.Play();
        }

        private void FullscreenVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            PlayAdjacentVideo(1);
        }

        private void FullscreenVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowToast("Fullscreen video playback failed");
            CloseFullscreenVideo_Click(null, null);
        }


        private void SyncBackgroundPlayer()
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings.Values;
                string storedVideoId = localSettings.ContainsKey("CurrentVideoId") ? localSettings["CurrentVideoId"].ToString() : "";
                bool hasPlayerState = _appMediaPlayer != null &&
                    (_appMediaPlayer.CurrentState == MediaPlayerState.Playing || _appMediaPlayer.CurrentState == MediaPlayerState.Paused);

                if (hasPlayerState || !string.IsNullOrEmpty(storedVideoId))
                {
                    MiniPlayerContainer.Visibility = Visibility.Visible;
                    bool isPlaying = hasPlayerState && _appMediaPlayer.CurrentState == MediaPlayerState.Playing;
                    _imagePlaybackRequested = isPlaying;
                    _imagePlaybackStartPending = false;
                    _backgroundPositionIsCurrent = hasPlayerState;
                    SetPlayPauseIcon(isPlaying);

                    string title = localSettings.ContainsKey("CurrentTitle") ? localSettings["CurrentTitle"].ToString() : "Unknown";
                    string artist = localSettings.ContainsKey("CurrentArtist") ? localSettings["CurrentArtist"].ToString() : "Unknown";
                    string vid = localSettings.ContainsKey("CurrentVideoId") ? localSettings["CurrentVideoId"].ToString() : "";
                    string thumb = localSettings.ContainsKey("CurrentThumbnail") ? localSettings["CurrentThumbnail"].ToString() : "";

                    // [OPT-M8] Skip nếu track đã sync — tránh tạo BitmapImage mới khi resume
                    if (currentTrack != null && currentTrack.VideoId == vid)
                    {
                        return;
                    }

                    MiniTitle.Text = title; BigTitle.Text = title;
                    MiniArtist.Text = artist; BigArtist.Text = artist;

                    MenuTitle.Text = title;
                    MenuArtist.Text = artist;

                    if (!string.IsNullOrEmpty(thumb))
                    {
                        try
                        {
                            var bigBmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                            bigBmp.DecodePixelWidth = 480;
                            bigBmp.UriSource = new Uri(GetNowPlayingThumbnail(thumb), UriKind.Absolute);
                            BigCoverImage.ImageSource = bigBmp;
                            AlbumArtEntranceStoryboard.Begin();
                            MenuCoverImage.ImageSource = bigBmp;

                            var miniBmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                            miniBmp.DecodePixelWidth = 100;
                            miniBmp.UriSource = new Uri(GetSquareThumbnail(thumb), UriKind.Absolute);
                            MiniCoverImage.ImageSource = miniBmp;
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(vid))
                    {
                        if (!_showVideoSurface && currentTrack != null && currentTrack.VideoId != vid)
                        {
                            _backgroundPositionIsCurrent = false;
                            try { ForegroundVideo.Stop(); } catch { }
                            ForegroundVideo.Source = null;
                            _videoPositionBeforeSuspend = TimeSpan.Zero;
                            _pendingVideoStartPosition = TimeSpan.Zero;
                        }
                        currentTrack = new YouTubeTrack { VideoId = vid, Title = title, ChannelName = artist, ThumbnailUrl = thumb };
                        bool isFav = favoriteTracks.Any(t => t.VideoId == vid);
                        BigHeartBtn.Content = isFav ? "♥" : "♡";
                        BigHeartBtn.Foreground = isFav ? _greenBrush : _whiteBrush;

                        var ignored = UpdateLyricsAsync(title, artist);
                        UpdateNowPlayingGradient(title, artist);
                    }
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task ResumeBackgroundAudioIfNeededAsync()
        {
            if (_showVideoSurface || currentTrack == null || currentTrack.VideoId.StartsWith("LOCAL:")) return;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                bool wasPlaying = values.ContainsKey("BackgroundWasPlaying") && (bool)values["BackgroundWasPlaying"];
                if (!wasPlaying) return;

                if (_appMediaPlayer == null || _appMediaPlayer.CurrentState == MediaPlayerState.Closed)
                {
                    _appMediaPlayer = BackgroundMediaPlayer.Current;
                    string audioUrl = await InnerTubeClient.ResolveStreamUrlAsync(currentTrack.VideoId, false) ?? "";
                    if (string.IsNullOrEmpty(audioUrl)) return;
                    long ticks = values.ContainsKey("BackgroundPositionTicks")
                        ? (long)values["BackgroundPositionTicks"] : 0L;
                    var message = new ValueSet {
                        { "UpdatePlaylist", "" },
                        { "Urls", new[] { audioUrl } },
                        { "Titles", new[] { currentTrack.Title ?? "" } },
                        { "Artists", new[] { currentTrack.ChannelName ?? "" } },
                        { "VideoIds", new[] { currentTrack.VideoId ?? "" } },
                        { "Thumbnails", new[] { currentTrack.ThumbnailUrl ?? "" } },
                        { "StartIndex", 0 },
                        { "FastUrl", audioUrl },
                        { "StartPositionTicks", ticks }
                    };
                    SendPlaylistToBackground(message);
                }
                else if (_appMediaPlayer.CurrentState == MediaPlayerState.Paused)
                {
                    _imagePlaybackRequested = true;
                    SendImageTransportCommand("ResumePlaybackMessage");
                }
            }
            catch { }
        }

        private async void BackgroundMediaPlayer_MessageReceivedFromBackground(object sender, MediaPlayerDataReceivedEventArgs e)
        {
            if (e.Data.ContainsKey("ForegroundVideoModeAccepted"))
            {
                try
                {
                    long acceptedGeneration = e.Data.ContainsKey(
                        "PlaybackCommandGeneration")
                        ? Convert.ToInt64(e.Data["PlaybackCommandGeneration"]) : 0L;
                    bool acceptedMode = (bool)e.Data["ForegroundVideoModeAccepted"];
                    if (_pendingForegroundVideoModeCommand != null &&
                        _pendingForegroundVideoModeCommand.ContainsKey(
                            "PlaybackCommandGeneration") &&
                        _pendingForegroundVideoModeCommand.ContainsKey(
                            "SetForegroundVideoMode") &&
                        acceptedGeneration == Convert.ToInt64(
                            _pendingForegroundVideoModeCommand[
                                "PlaybackCommandGeneration"]) &&
                        acceptedMode == (bool)_pendingForegroundVideoModeCommand[
                            "SetForegroundVideoMode"])
                    {
                        _pendingForegroundVideoModeCommand = null;
                    }
                }
                catch { }
                return;
            }

            if (e.Data.ContainsKey("BackgroundPlaybackStateChanged"))
            {
                string playbackVideoId = e.Data.ContainsKey("PlaybackVideoId")
                    ? e.Data["PlaybackVideoId"].ToString() : "";
                string playbackState = e.Data.ContainsKey("PlaybackState")
                    ? e.Data["PlaybackState"].ToString() : "";
                string backgroundInstanceId = e.Data.ContainsKey(
                    "BackgroundInstanceId")
                    ? e.Data["BackgroundInstanceId"].ToString() : "";
                bool isIntentOnly = e.Data.ContainsKey("PlaybackIntentOnly") &&
                    (bool)e.Data["PlaybackIntentOnly"];
                bool playbackRequested = e.Data.ContainsKey("PlaybackRequested") &&
                    (bool)e.Data["PlaybackRequested"];
                long playlistRequestId = 0;
                long stateGeneration = 0;
                long stateSequence = 0;
                try
                {
                    if (e.Data.ContainsKey("PlaylistRequestId"))
                        playlistRequestId = Convert.ToInt64(e.Data["PlaylistRequestId"]);
                    if (e.Data.ContainsKey("PlaybackStateGeneration"))
                        stateGeneration = Convert.ToInt64(
                            e.Data["PlaybackStateGeneration"]);
                    if (e.Data.ContainsKey("PlaybackStateSequence"))
                        stateSequence = Convert.ToInt64(
                            e.Data["PlaybackStateSequence"]);
                }
                catch { }
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!string.IsNullOrEmpty(backgroundInstanceId) &&
                        backgroundInstanceId != _backgroundPlaybackStateInstanceId)
                    {
                        _backgroundPlaybackStateInstanceId = backgroundInstanceId;
                        _lastBackgroundPlaybackStateGeneration = 0;
                        _lastBackgroundPlaybackStateSequence = 0;
                    }
                    if (stateGeneration > 0)
                    {
                        if (stateGeneration < _lastBackgroundPlaybackStateGeneration ||
                            stateGeneration < _lastPlaybackCommandGeneration ||
                            (stateGeneration == _lastBackgroundPlaybackStateGeneration &&
                             stateSequence <= _lastBackgroundPlaybackStateSequence))
                            return;
                    }
                    if (stateGeneration > _lastBackgroundPlaybackStateGeneration)
                    {
                        _lastBackgroundPlaybackStateGeneration = stateGeneration;
                        _lastBackgroundPlaybackStateSequence = stateSequence;
                    }
                    else if (stateGeneration == _lastBackgroundPlaybackStateGeneration &&
                        stateSequence > _lastBackgroundPlaybackStateSequence)
                    {
                        _lastBackgroundPlaybackStateSequence = stateSequence;
                    }
                    if (stateGeneration > _lastPlaybackCommandGeneration)
                        _lastPlaybackCommandGeneration = stateGeneration;

                    bool requestMatches = isIntentOnly || playlistRequestId <= 0 ||
                        playlistRequestId == _currentImagePlaylistRequestId;
                    bool videoMatches = isIntentOnly || currentTrack == null ||
                        currentTrack.VideoId == playbackVideoId;
                    if (!_showVideoSurface && !IsVideoSurfaceModeRequested() &&
                        requestMatches && videoMatches)
                    {
                        _imagePlaybackRequested = playbackRequested;
                        if (!playbackRequested)
                        {
                            _imagePlaybackStartPending = false;
                            _backgroundPositionIsCurrent = playbackState == "Paused";
                            SetPlayPauseIcon(false);
                        }
                        else if (playbackState == "Playing")
                        {
                            _imagePlaybackStartPending = false;
                            _backgroundPositionIsCurrent = true;
                            SetPlayPauseIcon(true);
                        }
                        else
                        {
                            _imagePlaybackStartPending = true;
                            _backgroundPositionIsCurrent = false;
                            SetPlayPauseIcon(true);
                        }
                    }
                });
                return;
            }

            if (e.Data.ContainsKey("TransportRejected"))
            {
                try
                {
                    long rejectedGeneration = e.Data.ContainsKey("PlaybackCommandGeneration")
                        ? Convert.ToInt64(e.Data["PlaybackCommandGeneration"]) : 0L;
                    long currentGeneration = e.Data.ContainsKey("CurrentPlaybackCommandGeneration")
                        ? Convert.ToInt64(e.Data["CurrentPlaybackCommandGeneration"]) : 0L;
                    if (_pendingBackgroundTransportCommand != null &&
                        _pendingBackgroundTransportCommand.ContainsKey("PlaybackCommandGeneration") &&
                        rejectedGeneration == Convert.ToInt64(
                            _pendingBackgroundTransportCommand["PlaybackCommandGeneration"]))
                    {
                        if (currentGeneration > _lastPlaybackCommandGeneration)
                            _lastPlaybackCommandGeneration = currentGeneration;
                        long rebasedGeneration = NextPlaybackCommandGeneration();
                        _pendingBackgroundTransportCommand["PlaybackCommandGeneration"] =
                            rebasedGeneration;
                        ValueSet pendingCommand = _pendingBackgroundTransportCommand;
                        if (_pendingBackgroundPlaylist == null)
                            BackgroundMediaPlayer.SendMessageToBackground(pendingCommand);
                        else
                            BackgroundMediaPlayer.SendMessageToBackground(
                                new ValueSet { { "Ping", "" } });
                        RetryPendingTransportCommand(pendingCommand);
                    }
                }
                catch
                {
                    try
                    {
                        BackgroundMediaPlayer.SendMessageToBackground(
                            new ValueSet { { "Ping", "" } });
                    }
                    catch { }
                }
                return;
            }

            if (e.Data.ContainsKey("TransportAccepted"))
            {
                long acceptedGeneration = 0;
                try
                {
                    if (e.Data.ContainsKey("PlaybackCommandGeneration"))
                        acceptedGeneration = Convert.ToInt64(
                            e.Data["PlaybackCommandGeneration"]);
                    if (_pendingBackgroundTransportCommand != null &&
                        _pendingBackgroundTransportCommand.ContainsKey(
                            "PlaybackCommandGeneration") &&
                        acceptedGeneration == Convert.ToInt64(
                            _pendingBackgroundTransportCommand[
                                "PlaybackCommandGeneration"]))
                    {
                        _pendingBackgroundTransportCommand = null;
                    }
                }
                catch { }
                return;
            }

            if (e.Data.ContainsKey("PlaylistAccepted"))
            {
                long acceptedRequestId = 0;
                bool acceptedPendingPlaylist = false;
                try
                {
                    if (e.Data.ContainsKey("PlaylistRequestId"))
                        acceptedRequestId = Convert.ToInt64(e.Data["PlaylistRequestId"]);
                    if (_pendingBackgroundPlaylist != null &&
                        _pendingBackgroundPlaylist.ContainsKey("PlaylistRequestId") &&
                        acceptedRequestId == Convert.ToInt64(
                            _pendingBackgroundPlaylist["PlaylistRequestId"]))
                    {
                        _pendingBackgroundPlaylist = null;
                        acceptedPendingPlaylist = true;
                    }
                }
                catch { }
                if (acceptedPendingPlaylist &&
                    _pendingBackgroundTransportCommand != null &&
                    !_showVideoSurface && !IsVideoSurfaceModeRequested())
                {
                    try
                    {
                        BackgroundMediaPlayer.SendMessageToBackground(
                            _pendingBackgroundTransportCommand);
                    }
                    catch
                    {
                        try
                        {
                            BackgroundMediaPlayer.SendMessageToBackground(
                                new ValueSet { { "Ping", "" } });
                        }
                        catch { }
                    }
                }
                return;
            }

            if (e.Data.ContainsKey("VideoTransportCommand"))
            {
                string command = e.Data["VideoTransportCommand"].ToString();
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    HandleVideoTransportCommand(command);
                });
                return;
            }

            if (e.Data.ContainsKey("BackgroundReady") && _showVideoSurface &&
                IsVideoSurfaceModeRequested())
            {
                MediaElement activeVideo = GetActiveVideoPlayer();
                SendForegroundVideoMode(true,
                    activeVideo != null && activeVideo.Source != null &&
                    IsVideoPlaybackActive(activeVideo));
            }
            else if (e.Data.ContainsKey("BackgroundReady") && _pendingBackgroundPlaylist != null &&
                !_showVideoSurface && !IsVideoSurfaceModeRequested())
            {
                ValueSet pendingPlaylist = _pendingBackgroundPlaylist;
                try
                {
                    long commandGeneration = Convert.ToInt64(
                        pendingPlaylist["PlaybackCommandGeneration"]);
                    SendForegroundVideoMode(false, false, commandGeneration, true);
                    BackgroundMediaPlayer.SendMessageToBackground(pendingPlaylist);
                }
                catch
                {
                    try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "Ping", "" } }); } catch { }
                }
            }

            if (e.Data.ContainsKey("BackgroundReady") &&
                _pendingBackgroundTransportCommand != null &&
                _pendingBackgroundPlaylist == null &&
                !_showVideoSurface && !IsVideoSurfaceModeRequested())
            {
                try
                {
                    BackgroundMediaPlayer.SendMessageToBackground(
                        _pendingBackgroundTransportCommand);
                }
                catch
                {
                    try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "Ping", "" } }); } catch { }
                }
            }

            if (e.Data.ContainsKey("ToastMessage"))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ShowToast(e.Data["ToastMessage"].ToString());
                });
            }

            if (e.Data.ContainsKey("TrackChanged"))
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    MiniPlayerContainer.Visibility = Visibility.Visible;
                    string title = e.Data["NewTitle"]?.ToString() ?? "";
                    string artist = e.Data["NewArtist"]?.ToString() ?? "";
                    string vid = e.Data.ContainsKey("NewVideoId") ? e.Data["NewVideoId"].ToString() : "";
                    string thumb = e.Data.ContainsKey("NewThumbnail") ? e.Data["NewThumbnail"].ToString() : "";

                    MiniTitle.Text = title; BigTitle.Text = title;
                    MiniArtist.Text = artist; BigArtist.Text = artist;

                    MenuTitle.Text = title;
                    MenuArtist.Text = artist;

                    if (!string.IsNullOrEmpty(thumb))
                    {
                        try
                        {
                            var bigBmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                            bigBmp.DecodePixelWidth = 480;
                            bigBmp.UriSource = new Uri(GetNowPlayingThumbnail(thumb), UriKind.Absolute);
                            BigCoverImage.ImageSource = bigBmp;
                            AlbumArtEntranceStoryboard.Begin();
                            MenuCoverImage.ImageSource = bigBmp;

                            var miniBmp = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                            miniBmp.DecodePixelWidth = 100;
                            miniBmp.UriSource = new Uri(GetSquareThumbnail(thumb), UriKind.Absolute);
                            MiniCoverImage.ImageSource = miniBmp;
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(vid))
                    {
                        if (!_showVideoSurface && currentTrack != null && currentTrack.VideoId != vid)
                        {
                            _backgroundPositionIsCurrent = false;
                            try { ForegroundVideo.Stop(); } catch { }
                            ForegroundVideo.Source = null;
                            _videoPositionBeforeSuspend = TimeSpan.Zero;
                            _pendingVideoStartPosition = TimeSpan.Zero;
                        }
                        currentTrack = new YouTubeTrack { VideoId = vid, Title = title, ChannelName = artist, ThumbnailUrl = thumb };

                        bool isFav = favoriteTracks.Any(t => t.VideoId == vid);
                        BigHeartBtn.Content = isFav ? "♥" : "♡";
                        BigHeartBtn.Foreground = isFav ? _greenBrush : _whiteBrush;

                        var ignored = UpdateLyricsAsync(title, artist);
                        UpdateNowPlayingGradient(title, artist);
                        YTMusicWP.Services.TileService.UpdateNowPlayingWithQueue(title, artist, thumb, currentQueueTracks);

                        // Restart marquee if NowPlaying is visible
                        if (NowPlayingView.Visibility == Visibility.Visible)
                        {
                            var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => StartTitleMarquee());
                        }
                    }
                });
            }
        }

        // ── Genre-Based Animated Gradient for Now Playing ──
        private DispatcherTimer _gradientPulseTimer;
        private Windows.UI.Color _currentGradientColor = Windows.UI.Color.FromArgb(255, 30, 50, 70);
        private bool _gradientPulseUp = true;

        private void UpdateNowPlayingGradient(string title, string artist)
        {
            // Detect genre from title/artist keywords → Spotify-like gradient colors
            string combined = (title + " " + artist).ToLowerInvariant();
            Windows.UI.Color topColor;

            if (combined.Contains("kpop") || combined.Contains("k-pop") || combined.Contains("bts") || combined.Contains("blackpink") || combined.Contains("twice"))
                topColor = Windows.UI.Color.FromArgb(255, 180, 80, 200);   // Purple-pink
            else if (combined.Contains("jpop") || combined.Contains("j-pop") || combined.Contains("anime"))
                topColor = Windows.UI.Color.FromArgb(255, 225, 17, 140);   // Hot pink
            else if (combined.Contains("lofi") || combined.Contains("chill") || combined.Contains("jazz"))
                topColor = Windows.UI.Color.FromArgb(255, 40, 100, 120);   // Teal
            else if (combined.Contains("edm") || combined.Contains("electronic") || combined.Contains("house"))
                topColor = Windows.UI.Color.FromArgb(255, 80, 155, 245);   // Electric blue
            else if (combined.Contains("hip hop") || combined.Contains("rap") || combined.Contains("trap"))
                topColor = Windows.UI.Color.FromArgb(255, 140, 25, 50);    // Dark red
            else if (combined.Contains("rock") || combined.Contains("metal") || combined.Contains("punk"))
                topColor = Windows.UI.Color.FromArgb(255, 80, 60, 60);     // Dark brown
            else if (combined.Contains("pop") || combined.Contains("hit") || combined.Contains("top"))
                topColor = Windows.UI.Color.FromArgb(255, 29, 185, 84);    // Spotify green
            else if (combined.Contains("indie") || combined.Contains("alternative"))
                topColor = Windows.UI.Color.FromArgb(255, 180, 156, 200);  // Soft lavender
            else if (combined.Contains("classical") || combined.Contains("piano") || combined.Contains("orchestra"))
                topColor = Windows.UI.Color.FromArgb(255, 50, 50, 100);    // Deep navy
            else if (combined.Contains("latin") || combined.Contains("reggaeton") || combined.Contains("salsa"))
                topColor = Windows.UI.Color.FromArgb(255, 225, 20, 41);    // Vibrant red
            else if (combined.Contains("phonk") || combined.Contains("drift"))
                topColor = Windows.UI.Color.FromArgb(255, 100, 20, 80);    // Deep magenta
            else if (combined.Contains("r&b") || combined.Contains("soul") || combined.Contains("rnb"))
                topColor = Windows.UI.Color.FromArgb(255, 80, 55, 80);     // Plum
            else
                topColor = Windows.UI.Color.FromArgb(255, 30, 50, 70);     // Default dark blue-gray

            _currentGradientColor = topColor;

            try
            {
                // Smooth color transition animation
                var storyboard = new Windows.UI.Xaml.Media.Animation.Storyboard();
                var colorAnim = new Windows.UI.Xaml.Media.Animation.ColorAnimation
                {
                    To = topColor,
                    Duration = new Duration(TimeSpan.FromMilliseconds(800)),
                    EasingFunction = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseInOut }
                };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(colorAnim, NowPlayingGradientTop);
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(colorAnim, "Color");
                storyboard.Children.Add(colorAnim);
                storyboard.Begin();

                // Start ambient pulse animation
                StartGradientPulse();
            }
            catch { }
        }

        private void StartGradientPulse()
        {
            if (_gradientPulseTimer != null)
            {
                _gradientPulseTimer.Stop();
                _gradientPulseTimer.Tick -= GradientPulse_Tick;
            }
            // Invalidate cached gradient storyboard so it re-targets after color change
            _gradientPulseSb = null;
            _gradientPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _gradientPulseTimer.Tick += GradientPulse_Tick;
            _gradientPulseTimer.Start();
        }

        // ═══════════════════════════════════════════════════════
        // Lyrics animation helpers
        // WP8.1: Reuse dedicated Storyboard & Animation objects per animation channel
        // to avoid GC churn and UI stutters on 512MB devices.
        // ═══════════════════════════════════════════════════════
        private static readonly Duration _dur400 = new Duration(TimeSpan.FromMilliseconds(400));
        private static readonly Duration _dur450 = new Duration(TimeSpan.FromMilliseconds(450));
        private static readonly Duration _dur500 = new Duration(TimeSpan.FromMilliseconds(500));

        private Windows.UI.Xaml.Media.Animation.Storyboard _lyricOutSb;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricOutOpAnim;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricOutSxAnim;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricOutSyAnim;

        private void AnimateLyricOut(FrameworkElement container, Windows.UI.Xaml.Media.ScaleTransform scale)
        {
            if (_lyricOutSb == null)
            {
                var easeInOut = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseInOut };
                _lyricOutOpAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.35, Duration = _dur500, EasingFunction = easeInOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricOutOpAnim, "Opacity");

                _lyricOutSxAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.85, Duration = _dur450, EasingFunction = easeInOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricOutSxAnim, "ScaleX");

                _lyricOutSyAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { To = 0.85, Duration = _dur450, EasingFunction = easeInOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricOutSyAnim, "ScaleY");

                _lyricOutSb = new Windows.UI.Xaml.Media.Animation.Storyboard();
                _lyricOutSb.Children.Add(_lyricOutOpAnim);
                _lyricOutSb.Children.Add(_lyricOutSxAnim);
                _lyricOutSb.Children.Add(_lyricOutSyAnim);
            }
            _lyricOutSb.Stop();
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricOutOpAnim, container);
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricOutSxAnim, scale);
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricOutSyAnim, scale);
            _lyricOutSb.Begin();
        }

        private Windows.UI.Xaml.Media.Animation.Storyboard _lyricInSb;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricInOpAnim;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricInSxAnim;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricInSyAnim;

        private void AnimateLyricIn(FrameworkElement container, Windows.UI.Xaml.Media.ScaleTransform scale)
        {
            if (_lyricInSb == null)
            {
                var easeOut = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseOut };
                _lyricInOpAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { From = 0.35, To = 1.0, Duration = _dur450, EasingFunction = easeOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricInOpAnim, "Opacity");

                _lyricInSxAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = _dur400, EasingFunction = easeOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricInSxAnim, "ScaleX");

                _lyricInSyAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { To = 1.0, Duration = _dur400, EasingFunction = easeOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricInSyAnim, "ScaleY");

                _lyricInSb = new Windows.UI.Xaml.Media.Animation.Storyboard();
                _lyricInSb.Children.Add(_lyricInOpAnim);
                _lyricInSb.Children.Add(_lyricInSxAnim);
                _lyricInSb.Children.Add(_lyricInSyAnim);
            }
            _lyricInSb.Stop();
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricInOpAnim, container);
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricInSxAnim, scale);
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricInSyAnim, scale);
            _lyricInSb.Begin();
        }

        private Windows.UI.Xaml.Media.Animation.Storyboard _lyricFsOpSb;
        private Windows.UI.Xaml.Media.Animation.DoubleAnimation _lyricFsOpAnim;

        private void AnimateOpacity(FrameworkElement target, double toValue)
        {
            if (_lyricFsOpSb == null)
            {
                var easeInOut = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseInOut };
                _lyricFsOpAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { Duration = _dur400, EasingFunction = easeInOut };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_lyricFsOpAnim, "Opacity");

                _lyricFsOpSb = new Windows.UI.Xaml.Media.Animation.Storyboard();
                _lyricFsOpSb.Children.Add(_lyricFsOpAnim);
            }
            _lyricFsOpSb.Stop();
            _lyricFsOpAnim.To = toValue;
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_lyricFsOpAnim, target);
            _lyricFsOpSb.Begin();
        }

        // [OPT-6] Cached gradient pulse objects
        private Windows.UI.Xaml.Media.Animation.Storyboard _gradientPulseSb;
        private Windows.UI.Xaml.Media.Animation.ColorAnimation _gradientPulseAnim;

        private void GradientPulse_Tick(object sender, object e)
        {
            try
            {
                var baseColor = _currentGradientColor;
                int shift = _gradientPulseUp ? 15 : -15;
                byte r = (byte)Math.Max(0, Math.Min(255, baseColor.R + shift));
                byte g = (byte)Math.Max(0, Math.Min(255, baseColor.G + shift));
                byte b = (byte)Math.Max(0, Math.Min(255, baseColor.B + shift));
                var targetColor = Windows.UI.Color.FromArgb(255, r, g, b);

                // Reuse storyboard + animation — only update To value
                if (_gradientPulseSb == null)
                {
                    _gradientPulseAnim = new Windows.UI.Xaml.Media.Animation.ColorAnimation
                    {
                        Duration = new Duration(TimeSpan.FromSeconds(3)),
                        EasingFunction = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseInOut }
                    };
                    Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(_gradientPulseAnim, NowPlayingGradientTop);
                    Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(_gradientPulseAnim, "Color");
                    _gradientPulseSb = new Windows.UI.Xaml.Media.Animation.Storyboard();
                    _gradientPulseSb.Children.Add(_gradientPulseAnim);
                }
                _gradientPulseSb.Stop();
                _gradientPulseAnim.To = targetColor;
                _gradientPulseSb.Begin();

                _gradientPulseUp = !_gradientPulseUp;
            }
            catch { }
        }

    }
}
