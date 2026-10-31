using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace YTMusicWP
{
    public sealed partial class MainPage
    {
        private async Task LoadHomeTrendingAsync()
        {
            HomeLoading.Visibility = Visibility.Visible;
            try
            {
                var trending = await InnerTubeClient.BrowseTrendingAsync();
                homeTracks.Clear();
                foreach (var t in trending)
                {
                    if (IsMusicTrack(t)) homeTracks.Add(t);
                }

                if (homeTracks.Count > 0)
                    YTMusicWP.Services.TileService.UpdateRecommendations(homeTracks, favoriteTracks, historyTracks);
                else
                    ShowToast("Couldn't load Trending");
            }
            catch
            {
                ShowToast("Couldn't load Trending");
            }
            HomeLoading.Visibility = Visibility.Collapsed;
        }

        private static bool IsMusicTrack(YouTubeTrack t)
        {
            if (t.VideoId == null || t.VideoId.StartsWith("CHANNEL:") || t.VideoId.StartsWith("PLAYLIST:")) return false;
            string ch = (t.ChannelName ?? "").ToLowerInvariant();
            if (ch == "episode" || ch == "podcast" || ch == "audiobook" || ch == "short stories") return false;
            string title = (t.Title ?? "").ToLowerInvariant();
            if (title.Contains("(storyteller)") || title.Contains("full audiobook") || title.Contains("full audio book")) return false;
            return true;
        }

        // ==========================================
        // HOME CHIP FILTERS
        // ==========================================
        private static readonly Windows.UI.Xaml.Media.SolidColorBrush _chipInactiveBrush =
            new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51));
        private static readonly Windows.UI.Xaml.Media.SolidColorBrush _chipActiveBrush =
            new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 29, 185, 84));

        private void SetHomeChipActive(Border active)
        {
            HomeChipAll.Background = _chipInactiveBrush;
            HomeChipMusic.Background = _chipInactiveBrush;
            HomeChipPodcasts.Background = _chipInactiveBrush;
            HomeChipAudiobooks.Background = _chipInactiveBrush;
            active.Background = _chipActiveBrush;
        }

        private void ShowHomePanel(string panel)
        {
            HomeMusicPanel.Visibility = panel == "music" ? Visibility.Visible : Visibility.Collapsed;
            HomePodcastPanel.Visibility = panel == "podcasts" ? Visibility.Visible : Visibility.Collapsed;
            HomeAudiobookPanel.Visibility = panel == "audiobooks" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HomeChipAll_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            SetHomeChipActive(HomeChipAll);
            ShowHomePanel("music");
        }

        private void HomeChipMusic_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            SetHomeChipActive(HomeChipMusic);
            ShowHomePanel("music");
        }

        private void HomeChipPodcasts_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            SetHomeChipActive(HomeChipPodcasts);
            ShowHomePanel("podcasts");
            if (podcastTracks.Count == 0)
            {
                var ignored = LoadPodcasts();
            }
        }

        private void HomeChipAudiobooks_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            SetHomeChipActive(HomeChipAudiobooks);
            ShowHomePanel("audiobooks");
            if (audiobookTracks.Count == 0)
            {
                var ignored = LoadAudiobooks();
            }
        }

        private async Task LoadPodcasts()
        {
            PodcastLoading.Visibility = Visibility.Visible;
            try
            {
                string region = InnerTubeClient.CurrentRegion;

                string query = "popular podcasts";
                switch (region)
                {
                    case "VN": query = "podcast tiếng Việt hay nhất"; break;
                    case "KR": query = "인기 팟캐스트 한국"; break;
                    case "JP": query = "人気ポッドキャスト 日本"; break;
                    case "GB": query = "top podcasts UK"; break;
                }

                var results = await InnerTubeClient.SearchAsync(query, 30);
                podcastTracks.Clear();
                if (results != null)
                {
                    foreach (var t in results)
                    {
                        if (t.VideoId != null && !t.VideoId.StartsWith("CHANNEL:"))
                            podcastTracks.Add(t);
                    }
                }
                PodcastList.ItemsSource = podcastTracks;
            }
            catch { }
            PodcastLoading.Visibility = Visibility.Collapsed;
        }

        private async Task LoadAudiobooks()
        {
            AudiobookLoading.Visibility = Visibility.Visible;
            try
            {
                string region = InnerTubeClient.CurrentRegion;

                string query = "audiobook full length";
                switch (region)
                {
                    case "VN": query = "sách nói tiếng Việt full"; break;
                    case "KR": query = "오디오북 한국어"; break;
                    case "JP": query = "オーディオブック 日本語"; break;
                    case "GB": query = "audiobook full length english"; break;
                }

                var results = await InnerTubeClient.SearchAsync(query, 30);
                audiobookTracks.Clear();
                if (results != null)
                {
                    foreach (var t in results)
                    {
                        if (t.VideoId != null && !t.VideoId.StartsWith("CHANNEL:"))
                            audiobookTracks.Add(t);
                    }
                }
                AudiobookList.ItemsSource = audiobookTracks;
            }
            catch { }
            AudiobookLoading.Visibility = Visibility.Collapsed;
        }
    }
}
