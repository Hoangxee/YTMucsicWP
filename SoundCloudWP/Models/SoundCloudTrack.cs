using Newtonsoft.Json;

namespace SoundCloudWP.Models
{
    public sealed class SoundCloudTrack
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("artwork_url")]
        public string ArtworkUrl { get; set; }

        [JsonProperty("duration")]
        public long DurationMilliseconds { get; set; }

        [JsonProperty("user")]
        public SoundCloudUser User { get; set; }

        public string Artist { get { return User == null ? "Unknown artist" : User.Username; } }
        public string DurationText { get { var seconds = DurationMilliseconds / 1000; return (seconds / 60) + ":" + (seconds % 60).ToString("00"); } }
        public string DisplayArtwork { get { return string.IsNullOrEmpty(ArtworkUrl) ? null : ArtworkUrl.Replace("-large.", "-t300x300."); } }
    }

    public sealed class SoundCloudUser
    {
        [JsonProperty("username")]
        public string Username { get; set; }
    }
}
