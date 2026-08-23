using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SoundCloudWP
{
    public sealed partial class App : Application
    {
        public App() { InitializeComponent(); }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame { CacheSize = 1 };
                Window.Current.Content = frame;
            }
            if (frame.Content == null && !frame.Navigate(typeof(MainPage), e.Arguments))
                throw new Exception("Failed to create initial page");
            Window.Current.Activate();
        }
    }
}
