using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Butterfly.Views
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            LoadButterflyIcon();
        }

        private void LoadButterflyIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Assets", "butterfly.ico");
                if (File.Exists(iconPath))
                {
                    using (Icon icon = new Icon(iconPath))
                    {
                        BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        ButterflyImage.Source = bitmapSource;
                    }
                }
            }
            catch
            {
                // if icon fails, continue without it
            }
        }

        private void ButterflyImage_Loaded(object sender, RoutedEventArgs e)
        {
            var continuousFlightStoryboard = (Storyboard)Resources["ContinuousFlightAnimation"];
            continuousFlightStoryboard.Begin();
        }

        public void FadeOutAndClose()
        {
            var fadeOutStoryboard = (Storyboard)Resources["FadeOutAnimation"];
            fadeOutStoryboard.Completed += (s, e) =>
            {
                this.Close();
            };
            fadeOutStoryboard.Begin();
        }
    }
}

