using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.Web.Http;

namespace FacebookWebWP
{
    public sealed partial class MainPage : Page
    {
        private const string MobileUrl = "https://m.facebook.com/login/";
        private const string BasicUrl = "https://mbasic.facebook.com/login.php";
        private const string TouchUrl = "https://touch.facebook.com/login.php";
        private const string HomeUrl = "https://mbasic.facebook.com/home.php";
        private const string MessagesUrl = "https://mbasic.facebook.com/messages/";
        private const string ModernUserAgent = "Mozilla/5.0 (Linux; Android 13; Pixel 6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
        private const string BasicUserAgent = "Opera/9.80 (J2ME/MIDP; Opera Mini/8.0.35626/37.8069; U; en) Presto/2.12.423 Version/12.16";
        private string _currentUrl = MobileUrl;
        private bool _basicMode;

        public MainPage()
        {
            InitializeComponent();
            NavigateSpoofed(MobileUrl);
        }

        private void NavigateSpoofed(string url, bool basicMode = false)
        {
            _currentUrl = url;
            _basicMode = basicMode;
            var userAgent = basicMode ? BasicUserAgent : ModernUserAgent;
            StatusText.Text = "Opening " + new Uri(url).Host + " in " + (basicMode ? "basic HTML" : "login") + " mode…";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                request.Headers.TryAppendWithoutValidation("User-Agent", userAgent);
                request.Headers.TryAppendWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
                Browser.NavigateWithHttpRequestMessage(request);
            }
            catch (Exception ex) { StatusText.Text = "Request error: " + ex.Message; }
        }

        private void Mobile_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(MobileUrl); }
        private void Basic_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(BasicUrl); }
        private void Touch_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(TouchUrl); }
        private void Home_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(HomeUrl, true); }
        private void Messages_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(MessagesUrl, true); }
        private void Refresh_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(_currentUrl, _basicMode); }

        private void Browser_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            if (args.Uri != null) _currentUrl = args.Uri.AbsoluteUri;
            StatusText.Text = args.Uri == null ? "Navigation starting…" : "Loading " + args.Uri.Host + args.Uri.AbsolutePath;
        }

        private async void Browser_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) { StatusText.Text = "Navigation failed: " + args.WebErrorStatus; return; }
            try
            {
                var result = await Browser.InvokeScriptAsync("eval", new[] { "document.title + ' | elements: ' + (document.body ? document.body.children.length : 0)" });
                StatusText.Text = "Loaded: " + result;
            }
            catch { StatusText.Text = "Page loaded, but its document cannot be inspected."; }
        }

    }
}
