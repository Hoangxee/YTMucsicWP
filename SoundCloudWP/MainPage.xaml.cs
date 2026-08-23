using System;
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
        private const string ClientIdKey = "SoundCloudClientId";
        private const string AccessTokenKey = "SoundCloudAccessToken";
        private SoundCloudClient _client;

        public MainPage()
        {
            InitializeComponent();
            LoadClient();
        }

        private void LoadClient()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var clientId = values.ContainsKey(ClientIdKey) ? values[ClientIdKey] as string : "";
            var token = values.ContainsKey(AccessTokenKey) ? values[AccessTokenKey] as string : "";
            if (_client != null) _client.Dispose();
            _client = new SoundCloudClient(clientId, token);
            ClientIdBox.Text = clientId ?? "";
            AccessTokenBox.Password = token ?? "";
        }

        private async void Search_Click(object sender, RoutedEventArgs e)
        {
            var query = SearchBox.Text == null ? "" : SearchBox.Text.Trim();
            if (query.Length == 0) return;
            BusyRing.IsActive = true;
            StatusText.Text = "Searching…";
            try
            {
                var tracks = await _client.SearchTracksAsync(query);
                ResultsList.ItemsSource = tracks;
                StatusText.Text = tracks.Count + " tracks";
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { BusyRing.IsActive = false; }
        }

        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Search_Click(sender, new RoutedEventArgs());
        }

        private async void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var track = e.ClickedItem as SoundCloudTrack;
            if (track == null) return;
            NowTitle.Text = track.Title;
            NowDetail.Text = "Resolving stream…";
            try
            {
                var stream = await _client.GetBestStreamAsync(track.Id);
                NowDetail.Text = track.Artist + " • " + stream.Format;
                Player.SetSource(null, "");
                Player.Source = new Uri(stream.Url);
                Player.Play();
            }
            catch (Exception ex) { NowDetail.Text = ex.Message; }
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Playback opened successfully on this device.";
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            StatusText.Text = "Playback failed: " + e.ErrorMessage;
            NowDetail.Text = "Codec/stream test failed";
        }

        private void Settings_Click(object sender, RoutedEventArgs e) { SettingsPanel.Visibility = Visibility.Visible; }
        private void CloseSettings_Click(object sender, RoutedEventArgs e) { SettingsPanel.Visibility = Visibility.Collapsed; }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[ClientIdKey] = ClientIdBox.Text == null ? "" : ClientIdBox.Text.Trim();
            values[AccessTokenKey] = AccessTokenBox.Password == null ? "" : AccessTokenBox.Password.Trim();
            LoadClient();
            SettingsPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Credential saved. Search for a track to test the API.";
        }
    }
}
