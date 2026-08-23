using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Web.Http;

namespace SoundCloudWP.Services
{
    public sealed class SoundCloudClient
    {
        private readonly HttpClient _http = new HttpClient();

        public async Task<SoundCloudEmbed> GetOEmbedAsync(string soundCloudUrl)
        {
            var uri = new Uri("https://soundcloud.com/oembed?format=json&maxwidth=480&url=" + Uri.EscapeDataString(soundCloudUrl));
            using (var response = await _http.GetAsync(uri))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("SoundCloud oEmbed " + (int)response.StatusCode + ": " + ShortMessage(body));
                var json = JObject.Parse(body);
                return new SoundCloudEmbed { Title = (string)json["title"], ThumbnailUrl = (string)json["thumbnail_url"] };
            }
        }

        private static string ShortMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Request failed";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 140 ? text : text.Substring(0, 140);
        }
    }

    public sealed class SoundCloudEmbed
    {
        public string Title { get; set; }
        public string ThumbnailUrl { get; set; }
    }
}
