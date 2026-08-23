using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SoundCloudWP.Models;
using Windows.Web.Http;

namespace SoundCloudWP.Services
{
    public sealed class SoundCloudClient : IDisposable
    {
        private const string ApiBase = "https://api.soundcloud.com";
        private readonly HttpClient _http = new HttpClient();
        private readonly string _clientId;
        private readonly string _accessToken;

        public SoundCloudClient(string clientId, string accessToken)
        {
            _clientId = (clientId ?? "").Trim();
            _accessToken = (accessToken ?? "").Trim();
        }

        public async Task<IList<SoundCloudTrack>> SearchTracksAsync(string query)
        {
            var uri = BuildUri("/tracks?q=" + Uri.EscapeDataString(query) + "&limit=30&linked_partitioning=1");
            var json = await GetStringAsync(uri);
            var root = JObject.Parse(json);
            var collection = root["collection"] as JArray;
            return collection == null ? new List<SoundCloudTrack>() : collection.ToObject<List<SoundCloudTrack>>();
        }

        public async Task<SoundCloudStream> GetBestStreamAsync(long trackId)
        {
            var json = await GetStringAsync(BuildUri("/tracks/" + trackId + "/streams"));
            var streams = JObject.Parse(json);
            string[] preferred = { "http_mp3_128_url", "hls_mp3_128_url", "hls_aac_160_url", "hls_aac_96_url" };
            foreach (var format in preferred)
            {
                var url = (string)streams[format];
                if (!string.IsNullOrEmpty(url)) return new SoundCloudStream { Url = url, Format = format };
            }
            throw new InvalidOperationException("Track has no compatible stream URL.");
        }

        private Uri BuildUri(string path)
        {
            if (string.IsNullOrEmpty(_accessToken) && string.IsNullOrEmpty(_clientId))
                throw new InvalidOperationException("Enter a SoundCloud Client ID or access token in Settings.");
            var separator = path.Contains("?") ? "&" : "?";
            return new Uri(ApiBase + path + (string.IsNullOrEmpty(_accessToken) ? separator + "client_id=" + Uri.EscapeDataString(_clientId) : ""));
        }

        private async Task<string> GetStringAsync(Uri uri)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(_accessToken)) request.Headers.TryAppendWithoutValidation("Authorization", "OAuth " + _accessToken);
            using (var response = await _http.SendRequestAsync(request))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("SoundCloud API " + (int)response.StatusCode + ": " + ShortMessage(body));
                return body;
            }
        }

        private static string ShortMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Request failed";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 160 ? text : text.Substring(0, 160);
        }

        public void Dispose() { _http.Dispose(); }
    }

    public sealed class SoundCloudStream
    {
        public string Url { get; set; }
        public string Format { get; set; }
    }
}
