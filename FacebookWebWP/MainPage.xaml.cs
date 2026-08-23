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
        private const string ModernUserAgent = "Mozilla/5.0 (Linux; Android 13; Pixel 6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
        private string _currentUrl = MobileUrl;

        public MainPage()
        {
            InitializeComponent();
            NavigateSpoofed(MobileUrl);
        }

        private void NavigateSpoofed(string url)
        {
            _currentUrl = url;
            StatusText.Text = "Opening " + new Uri(url).Host + " with Android Chrome User-Agent…";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
                request.Headers.TryAppendWithoutValidation("User-Agent", ModernUserAgent);
                request.Headers.TryAppendWithoutValidation("Accept-Language", "vi-VN,vi;q=0.9,en-US;q=0.8,en;q=0.7");
                Browser.NavigateWithHttpRequestMessage(request);
            }
            catch (Exception ex) { StatusText.Text = "Request error: " + ex.Message; }
        }

        private void Mobile_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(MobileUrl); }
        private void Basic_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(BasicUrl); }
        private void Touch_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(TouchUrl); }
        private void Refresh_Click(object sender, RoutedEventArgs e) { NavigateSpoofed(_currentUrl); }

        private void Browser_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            if (args.Uri != null) _currentUrl = args.Uri.AbsoluteUri;
            StatusText.Text = args.Uri == null ? "Navigation starting…" : "Loading " + args.Uri.Host + args.Uri.AbsolutePath;
        }

        private void Browser_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            StatusText.Text = args.IsSuccess ? "Page loaded. Try signing in directly with Facebook." : "Navigation failed: " + args.WebErrorStatus;
        }

    }
}
