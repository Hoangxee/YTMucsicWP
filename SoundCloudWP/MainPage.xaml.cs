using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SoundCloudWP.Models;
using SoundCloudWP.Services;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace SoundCloudWP
{
    public sealed partial class MainPage : Page
    {
        private const string SavedItemsKey = "SavedSoundCloudItems";
        private readonly SoundCloudClient _client = new SoundCloudClient();
        private List<LocalSoundCloudItem> _savedItems = new List<LocalSoundCloudItem>();

        public MainPage()
        {
            InitializeComponent();
            LoadSavedItems();
            SearchBrowser.Navigate(new Uri("https://soundcloud.com/search/sounds"));
        }

        private void Search_Click(object sender, RoutedEventArgs e)
        {
            var query = (SearchBox.Text ?? "").Trim();
            if (query.Length == 0) return;
            ShowSearch();
            StatusText.Text = "Loading SoundCloud search…";
            SearchBrowser.Navigate(new Uri("https://soundcloud.com/search/sounds?q=" + Uri.EscapeDataString(query)));
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Search_Click(sender, new RoutedEventArgs());
        }

        private void SearchBrowser_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            if (IsPlayableSoundCloudUrl(args.Uri)) TrackUrlBox.Text = CleanUrl(args.Uri.AbsoluteUri);
        }

        private void SearchBrowser_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            StatusText.Text = args.IsSuccess ? "Open a track/playlist, or paste its URL below." : "SoundCloud web failed on WP8.1: " + args.WebErrorStatus;
        }

        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            var url = CleanUrl(TrackUrlBox.Text);
            if (!IsPlayableSoundCloudUrl(url)) { StatusText.Text = "Enter a public soundcloud.com track or playlist URL."; return; }
            StatusText.Text = "Loading official SoundCloud Widget…";
            try
            {
                var metadata = await _client.GetOEmbedAsync(url);
                PlayerTitle.Text = string.IsNullOrEmpty(metadata.Title) ? "SoundCloud Player" : metadata.Title;
                OpenWidget(url);
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            var url = CleanUrl(TrackUrlBox.Text);
            if (!IsPlayableSoundCloudUrl(url)) { StatusText.Text = "Enter a public soundcloud.com track or playlist URL."; return; }
            try
            {
                var metadata = await _client.GetOEmbedAsync(url);
                var existing = _savedItems.Find(x => string.Equals(x.SoundCloudUrl, url, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    _savedItems.Add(new LocalSoundCloudItem
                    {
                        SoundCloudUrl = url,
                        Title = string.IsNullOrEmpty(metadata.Title) ? UrlTitle(url) : metadata.Title,
                        ArtworkUrl = metadata.ThumbnailUrl,
                        AlbumName = string.IsNullOrWhiteSpace(AlbumNameBox.Text) ? "Favorites" : AlbumNameBox.Text.Trim()
                    });
                    SaveItems();
                    SavedList.ItemsSource = null;
                    SavedList.ItemsSource = _savedItems;
                }
                StatusText.Text = "Saved locally. Audio was not downloaded.";
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }

        private void SavedList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as LocalSoundCloudItem;
            if (item == null) return;
            TrackUrlBox.Text = item.SoundCloudUrl;
            PlayerTitle.Text = item.Title;
            OpenWidget(item.SoundCloudUrl);
        }

        private void OpenWidget(string soundCloudUrl)
        {
            LibraryPanel.Visibility = Visibility.Collapsed;
            PlayerPanel.Visibility = Visibility.Visible;
            var widgetUrl = "https://w.soundcloud.com/player/?url=" + Uri.EscapeDataString(soundCloudUrl) + "&auto_play=true&show_artwork=true&show_comments=false&show_playcount=true&show_user=true";
            PlayerBrowser.Navigate(new Uri(widgetUrl));
        }

        private void PlayerBrowser_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            StatusText.Text = args.IsSuccess ? "Widget loaded. Tap its play button if autoplay is blocked." : "Widget failed on WP8.1: " + args.WebErrorStatus;
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            PlayerBrowser.NavigateToString("<html><body style='background:#101010'></body></html>");
            PlayerPanel.Visibility = Visibility.Collapsed;
        }

        private void Library_Click(object sender, RoutedEventArgs e)
        {
            PlayerPanel.Visibility = Visibility.Collapsed;
            LibraryPanel.Visibility = LibraryPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            LibraryButton.Content = LibraryPanel.Visibility == Visibility.Visible ? "SEARCH" : "LIBRARY";
        }

        private void ShowSearch()
        {
            PlayerPanel.Visibility = Visibility.Collapsed;
            LibraryPanel.Visibility = Visibility.Collapsed;
            LibraryButton.Content = "LIBRARY";
        }

        private void LoadSavedItems()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.ContainsKey(SavedItemsKey)) _savedItems = JsonConvert.DeserializeObject<List<LocalSoundCloudItem>>(values[SavedItemsKey] as string) ?? new List<LocalSoundCloudItem>();
            }
            catch { _savedItems = new List<LocalSoundCloudItem>(); }
            SavedList.ItemsSource = _savedItems;
        }

        private void SaveItems() { ApplicationData.Current.LocalSettings.Values[SavedItemsKey] = JsonConvert.SerializeObject(_savedItems); }
        private static bool IsPlayableSoundCloudUrl(Uri uri) { return uri != null && IsPlayableSoundCloudUrl(uri.AbsoluteUri); }

        private static bool IsPlayableSoundCloudUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            if (host != "soundcloud.com" && host != "www.soundcloud.com" && host != "m.soundcloud.com") return false;
            var path = uri.AbsolutePath.Trim('/');
            return path.Length > 0 && !path.StartsWith("search", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("discover", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("you", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri)) return (value ?? "").Trim();
            return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        }

        private static string UrlTitle(string url)
        {
            var part = url.TrimEnd('/');
            var slash = part.LastIndexOf('/');
            if (slash >= 0) part = part.Substring(slash + 1);
            return Uri.UnescapeDataString(part).Replace('-', ' ');
        }
    }
}
