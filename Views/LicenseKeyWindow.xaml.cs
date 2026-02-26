using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Butterfly.Native;
using Butterfly.Services;

namespace Butterfly.Views
{
    public partial class LicenseKeyWindow : Window
    {
        private readonly LicenseService _licenseService;
        private const string LICENSE_FILE = "license.key";

        public LicenseKeyWindow()
        {
            InitializeComponent();
            _licenseService = new LicenseService();
            LoadAppIcon();
            
            this.Loaded += LicenseKeyWindow_Loaded;
            this.Activated += LicenseKeyWindow_Activated;
            this.GotFocus += LicenseKeyWindow_GotFocus;
            this.Deactivated += LicenseKeyWindow_Deactivated;
            
            UpdateWindowTitle();
            
            this.Unloaded += LicenseKeyWindow_Unloaded;
            
            App.OnLicenseTypeChanged += LicenseKeyWindow_OnLicenseTypeChanged;
        }
        
        private void LicenseKeyWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Butterfly.Helpers.FocusHelper.ForceForeground(this);
            LicenseKeyTextBox.Focus();
            
            UpdateWindowTitle();
        }

        private void LicenseKeyWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            App.OnLicenseTypeChanged -= LicenseKeyWindow_OnLicenseTypeChanged;
        }

        private void LicenseKeyWindow_OnLicenseTypeChanged(string newTier)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                UpdateWindowTitle();
            }));
        }
        
        private void LicenseKeyWindow_Activated(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LicenseKeyTextBox.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        
        private void LicenseKeyWindow_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!LicenseKeyTextBox.IsFocused)
            {
                LicenseKeyTextBox.Focus();
            }
        }
        
        private void LicenseKeyWindow_Deactivated(object? sender, EventArgs e)
        {
            this.Activated -= LicenseKeyWindow_Activated;
            this.Activated += LicenseKeyWindow_Activated;
        }

        private void LoadAppIcon()
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
                        AppIcon.Source = bitmapSource;
                    }
                }
            }
            catch
            {
                // if icon fails to load, just continue without it
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkTitleBar();
        }

        private void ApplyDarkTitleBar()
        {
            var windowHelper = new WindowInteropHelper(this);
            var hwnd = windowHelper.Handle;

            if (Environment.OSVersion.Version.Major >= 10 && hwnd != IntPtr.Zero)
            {
                int useImmersiveDarkMode = 1;

                if (Win32Service.DwmSetWindowAttribute(hwnd, Win32Service.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    Win32Service.DwmSetWindowAttribute(hwnd, Win32Service.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
        }

        public void SetStatusMessage(string message)
        {
            UpdateStatus(message, false);
        }

        private async void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            string licenseKey = LicenseKeyTextBox.Text.Trim();

            if (string.IsNullOrEmpty(licenseKey))
            {
                UpdateStatus(Butterfly.Services.LocalizationManager.GetString("License_PleaseEnterKey"), false);
                return;
            }

            ActivateButton.IsEnabled = false;
            UpdateStatus(Butterfly.Services.LocalizationManager.GetString("License_ValidatingKey"), true);

            var result = await _licenseService.ActivateLicenseAsync(licenseKey);

            if (result.IsValid)
            {
                if (!string.IsNullOrEmpty(result.Tier))
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        App.LicenseType = result.Tier;
                    });
                }
                
                try
                {
                    string licensePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LICENSE_FILE);
                    File.WriteAllText(licensePath, licenseKey);
                }
                catch (Exception ex)
                {
                    UpdateStatus(Butterfly.Services.LocalizationManager.GetString("License_ErrorSaving", ex.Message), false);
                    ActivateButton.IsEnabled = true;
                    return;
                }

                UpdateStatus(Butterfly.Services.LocalizationManager.GetString("License_ActivationSuccessful"), true);
                
                await Task.Delay(1000);
                
                OpenMainWindow();
            }
            else
            {
                try
                {
                    string licensePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LICENSE_FILE);
                    if (File.Exists(licensePath))
                    {
                        File.Delete(licensePath);
                    }
                }
                catch { }

                LicenseKeyTextBox.Text = "";
                UpdateStatus(result.Message, false);
                ActivateButton.IsEnabled = true;
            }
        }

        private void OpenMainWindow()
        {
            var mainWindow = new MainWindow();
            
            Application.Current.MainWindow = mainWindow;
            
            mainWindow.Show();
            mainWindow.Activate();
            
            this.Close();
        }

        private void UpdateStatus(string message, bool isInfo)
        {
            StatusLabel.Text = message;
            StatusLabel.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136));
        }

        private void DiscordLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch
            {
                // ignore errors opening browser
            }
        }

        private void LicenseKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var textBox = sender as System.Windows.Controls.TextBox;
            if (textBox != null)
            {
                CustomCaret.Visibility = string.IsNullOrEmpty(textBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            }

            string defaultMessage = Butterfly.Services.LocalizationManager.GetString("License_PleaseEnterKey");
            if (StatusLabel.Text != "" && StatusLabel.Text != defaultMessage)
            {
                StatusLabel.Text = defaultMessage;
            }
        }

        private void LicenseKeyTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LicenseKeyTextBox.Text))
            {
                CustomCaret.Visibility = Visibility.Visible;
            }
        }

        private void LicenseKeyTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CustomCaret.Visibility = Visibility.Collapsed;
            
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (this.IsActive)
                {
                    LicenseKeyTextBox.Focus();
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void LicenseKeyTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ActivateButton.IsEnabled)
            {
                ActivateButton_Click(sender, e);
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            LicenseKeyTextBox.Focus();
        }

        private void UpdateWindowTitle()
        {
            string fullTitle = App.GetFormattedTitle(App.LicenseType);

            this.Title = fullTitle;

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                Butterfly.Native.Win32Service.SetWindowText(helper.Handle, fullTitle);
            }
        }
    }
}

