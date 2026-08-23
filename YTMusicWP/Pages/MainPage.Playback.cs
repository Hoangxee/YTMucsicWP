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

        private void SendPlaylistToBackground(ValueSet message)
        {
            _pendingBackgroundPlaylist = message;
            try { BackgroundMediaPlayer.SendMessageToBackground(message); } catch { }
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

            try
            {
                if (_appMediaPlayer == null || _appMediaPlayer.CurrentState == MediaPlayerState.Closed)
                {
                    _appMediaPlayer = BackgroundMediaPlayer.Current;
                    _appMediaPlayer.CurrentStateChanged -= BackgroundMediaPlayer_CurrentStateChanged;
                    _appMediaPlayer.CurrentStateChanged += BackgroundMediaPlayer_CurrentStateChanged;
                }
                _appMediaPlayer.Pause();
            }
            catch { }
            try { ForegroundVideo.Stop(); } catch { }
            ForegroundVideo.Source = null;
            _showVideoSurface = false;

            if (!track.VideoId.StartsWith("LOCAL:") && !IsInternetAvailable()) { ShowToast("No Internet connection"); return; }

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
            else if (homeHistoryCarouselTracks.Contains(track)) activeList = homeHistoryCarouselTracks;
            else if (historyQuickGridTracks.Contains(track)) activeList = historyQuickGridTracks;
            else if (popTracks.Contains(track)) activeList = popTracks;
            else if (lofiTracks.Contains(track)) activeList = lofiTracks;
            else if (workoutTracks.Contains(track)) activeList = workoutTracks;
            else if (genre5Tracks.Contains(track)) activeList = genre5Tracks;
            else if (genre6Tracks.Contains(track)) activeList = genre6Tracks;
            else if (genre7Tracks.Contains(track)) activeList = genre7Tracks;
            else if (genre8Tracks.Contains(track)) activeList = genre8Tracks;
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
            RefreshHomeHistorySections();

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
                        ? "ms-appdata:///local/" + t.VideoId.Substring(6)
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
            SendPlaylistToBackground(message);
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
                    ? "ms-appdata:///local/" + t.VideoId.Substring(6)
                    : "";
                titles[i] = t.Title ?? "";
                artists[i] = t.ChannelName ?? "";
                videoIds[i] = t.VideoId ?? "";
                thumbnails[i] = t.ThumbnailUrl ?? "";
            }

            var message = new ValueSet {
                { "UpdatePlaylist", "" }, { "Urls", urls }, { "Titles", titles }, { "Artists", artists },
                { "VideoIds", videoIds }, { "Thumbnails", thumbnails }, { "StartIndex", relativeStartIndex }
            };
            SendPlaylistToBackground(message);
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
                        SetPlayPauseIcon(sender.CurrentState == MediaPlayerState.Playing);
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
            bool videoMode = SafeGetBool(ApplicationData.Current.LocalSettings.Values, "VideoMode", true) && _showVideoSurface;
            if (videoMode)
            {
                try
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
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
            if (session == null || session.CurrentState != MediaPlayerState.Playing) return;

            TimeSpan pos, dur;
            try { pos = session.Position; dur = session.NaturalDuration; } catch { return; }
            if (dur.TotalSeconds <= 0) return;

            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    try
                    {
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
                    if (_appMediaPlayer.CurrentState == MediaPlayerState.Paused) _appMediaPlayer.Play();
                }
            }
            catch { }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_showVideoSurface && ForegroundVideo.Source != null)
                {
                    MediaElement videoPlayer = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                    bool wasPlaying = videoPlayer.CurrentState == MediaElementState.Playing;
                    if (wasPlaying) videoPlayer.Pause();
                    else videoPlayer.Play();
                    SetPlayPauseIcon(!wasPlaying);
                    return;
                }
                // FIX: Nhận diện OS đã tự đóng Background Task (State = Closed) để ra lệnh phát lại từ đầu
                if (_appMediaPlayer.CurrentState == MediaPlayerState.Closed)
                {
                    if (currentTrack != null) PlayTrack(currentTrack);
                }
                else if (_appMediaPlayer.CurrentState == MediaPlayerState.Playing)
                {
                    _appMediaPlayer.Pause();
                }
                else
                {
                    _appMediaPlayer.Play();
                }
            }
            catch { }
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_showVideoSurface && ForegroundVideo.Source != null)
                {
                    MediaElement videoPlayer = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                    if (videoPlayer.Position.TotalSeconds > 3) { videoPlayer.Position = TimeSpan.Zero; videoPlayer.Play(); }
                    else PlayAdjacentVideo(-1);
                    return;
                }
                if (_appMediaPlayer.Position.TotalSeconds > 3)
                {
                    _appMediaPlayer.Position = TimeSpan.Zero;
                    _appMediaPlayer.Play();
                }
                else
                {
                    BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "PrevTrackMessage", "" } });
                }
            }
            catch { try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "PrevTrackMessage", "" } }); } catch { } }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_showVideoSurface && ForegroundVideo.Source != null)
                {
                    PlayAdjacentVideo(1);
                    return;
                }
                // Always send NextTrackMessage — AudioTask handles Closed state internally
                BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "NextTrackMessage", "" } });
            }
            catch
            {
                // If background task is truly dead, re-play current track to wake it up
                try { if (currentTrack != null) PlayTrack(currentTrack); } catch { }
            }
        }

        private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isFullscreenVideo) CloseFullscreenVideo_Click(null, null);
                ForegroundVideo.Stop();
                ForegroundVideo.Source = null;
                FullscreenVideo.Stop();
                FullscreenVideo.Source = null;
                try { _appMediaPlayer.Pause(); } catch { }
                try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "StopPlaybackMessage", "" } }); } catch { }
                try { BackgroundMediaPlayer.Shutdown(); } catch { }
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
        }

        private void PlayAdjacentVideo(int offset)
        {
            if (currentTrack == null || currentQueueTracks.Count == 0) return;
            int index = currentQueueTracks.IndexOf(currentTrack);
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
        private bool _backgroundHandoffStarted;
        private TimeSpan _pendingVideoStartPosition = TimeSpan.Zero;

        private async System.Threading.Tasks.Task HandoffVideoToBackgroundAsync()
        {
            if (!VideoModeToggle.IsOn || currentTrack == null) return;
            MediaElement player = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
            if (player.Source == null) return;
            _videoWasPlayingBeforeSuspend = player.CurrentState == MediaElementState.Playing ||
                player.CurrentState == MediaElementState.Buffering || player.CurrentState == MediaElementState.Opening;
            if (!_videoWasPlayingBeforeSuspend) return;

            _videoPositionBeforeSuspend = player.Position;
            _backgroundHandoffStarted = false;
            if (_handoffUrls == null || _handoffUrls.Length == 0)
            {
                string audioUrl = "";
                try { audioUrl = await InnerTubeClient.ResolveStreamUrlAsync(currentTrack.VideoId, false) ?? ""; }
                catch { }
                if (string.IsNullOrEmpty(audioUrl)) return;
                _handoffUrls = new[] { audioUrl };
                _handoffTitles = new[] { currentTrack.Title ?? "" };
                _handoffArtists = new[] { currentTrack.ChannelName ?? "" };
                _handoffVideoIds = new[] { currentTrack.VideoId ?? "" };
                _handoffThumbnails = new[] { currentTrack.ThumbnailUrl ?? "" };
                _handoffStartIndex = 0;
            }
            var message = new ValueSet {
                { "UpdatePlaylist", "" }, { "Urls", _handoffUrls }, { "Titles", _handoffTitles },
                { "Artists", _handoffArtists }, { "VideoIds", _handoffVideoIds },
                { "Thumbnails", _handoffThumbnails }, { "StartIndex", _handoffStartIndex },
                { "FastUrl", _handoffUrls[_handoffStartIndex] },
                { "StartPositionTicks", _videoPositionBeforeSuspend.Ticks }
            };
            try
            {
                _pendingBackgroundPlaylist = message;
                BackgroundMediaPlayer.SendMessageToBackground(message);
            }
            catch { return; }

            for (int i = 0; i < 15; i++)
            {
                await System.Threading.Tasks.Task.Delay(100);
                try
                {
                    if (_appMediaPlayer.CurrentState == MediaPlayerState.Playing)
                    {
                        _backgroundHandoffStarted = true;
                        player.Pause();
                        break;
                    }
                }
                catch { }
            }
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
                try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "PausePlaybackMessage", "" } }); } catch { }
                try { _appMediaPlayer.Pause(); } catch { }
                MediaElement player = _isFullscreenVideo ? FullscreenVideo : ForegroundVideo;
                if (player.Source == null && currentTrack != null && !currentTrack.VideoId.StartsWith("LOCAL:"))
                {
                    string videoUrl = await InnerTubeClient.ResolveStreamUrlAsync(currentTrack.VideoId, true) ?? "";
                    if (string.IsNullOrEmpty(videoUrl)) return;
                    _pendingVideoStartPosition = _videoPositionBeforeSuspend;
                    player.Source = new Uri(videoUrl, UriKind.Absolute);
                    return;
                }
                if (player.Source != null)
                {
                    player.Position = _videoPositionBeforeSuspend;
                    player.Play();
                    SetPlayPauseIcon(true);
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
            if (!VideoModeToggle.IsOn || currentTrack == null || currentTrack.VideoId.StartsWith("LOCAL:")) return;

            if (_showVideoSurface)
            {
                await HandoffVideoToBackgroundAsync();
                if (!_backgroundHandoffStarted)
                {
                    ShowToast("Unable to continue audio in background");
                    return;
                }

                _showVideoSurface = false;
                ForegroundVideo.Visibility = Visibility.Collapsed;
                NowPlayingCoverRect.Visibility = Visibility.Visible;
                VideoSurfaceToggleBtn.Content = "VIDEO";
                _videoWasPlayingBeforeSuspend = false;
                _backgroundHandoffStarted = false;
                SetPlayPauseIcon(true);
                return;
            }

            if (ForegroundVideo.Source == null)
            {
                TimeSpan position = TimeSpan.Zero;
                try { position = _appMediaPlayer.Position; } catch { }
                string videoUrl = "";
                try { videoUrl = await InnerTubeClient.ResolveStreamUrlAsync(currentTrack.VideoId, true) ?? ""; }
                catch { }
                if (string.IsNullOrEmpty(videoUrl)) { ShowToast("Unable to load video"); return; }
                try
                {
                    _pendingVideoStartPosition = position;
                    ForegroundVideo.Source = new Uri(videoUrl, UriKind.Absolute);
                }
                catch { }
            }
            else
            {
                try
                {
                    TimeSpan position = _appMediaPlayer.Position;
                    ForegroundVideo.Position = position;
                    ForegroundVideo.Play();
                    _appMediaPlayer.Pause();
                }
                catch { }
            }

            _showVideoSurface = true;
            _pendingBackgroundPlaylist = null;
            try { BackgroundMediaPlayer.SendMessageToBackground(new ValueSet { { "PausePlaybackMessage", "" } }); } catch { }
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
                ForegroundVideo.Position = _pendingVideoStartPosition;
                _pendingVideoStartPosition = TimeSpan.Zero;
                ForegroundVideo.Play();
                _appMediaPlayer.Pause();
                SetPlayPauseIcon(true);
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
            if (player == null) return;
            if ((_isFullscreenVideo && player != FullscreenVideo) || (!_isFullscreenVideo && player != ForegroundVideo)) return;
            if (player.CurrentState == MediaElementState.Playing) SetPlayPauseIcon(true);
            else if (player.CurrentState == MediaElementState.Paused || player.CurrentState == MediaElementState.Stopped) SetPlayPauseIcon(false);
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

                if (_appMediaPlayer.CurrentState == MediaPlayerState.Playing || _appMediaPlayer.CurrentState == MediaPlayerState.Paused)
                {
                    MiniPlayerContainer.Visibility = Visibility.Visible;
                    bool isPlaying = (_appMediaPlayer.CurrentState == MediaPlayerState.Playing);
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

        private async void BackgroundMediaPlayer_MessageReceivedFromBackground(object sender, MediaPlayerDataReceivedEventArgs e)
        {
            if (e.Data.ContainsKey("BackgroundReady") && _pendingBackgroundPlaylist != null)
            {
                try { BackgroundMediaPlayer.SendMessageToBackground(_pendingBackgroundPlaylist); } catch { }
            }

            if (e.Data.ContainsKey("PlaylistAccepted"))
            {
                _pendingBackgroundPlaylist = null;
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
