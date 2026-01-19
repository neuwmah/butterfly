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
            
            // Subscribe to window events for persistent focus
            this.Loaded += LicenseKeyWindow_Loaded;
            this.Activated += LicenseKeyWindow_Activated;
            this.GotFocus += LicenseKeyWindow_GotFocus;
            this.Deactivated += LicenseKeyWindow_Deactivated;
            
            // Update title when window loads
            UpdateWindowTitle();
            
            // Add Unloaded event to unsubscribe and prevent memory leaks
            this.Unloaded += LicenseKeyWindow_Unloaded;
            
            // Subscribe to LicenseType change event
            App.OnLicenseTypeChanged += LicenseKeyWindow_OnLicenseTypeChanged;
        }
        
        private void LicenseKeyWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Butterfly.Helpers.FocusHelper.ForceForeground(this);
            // Focus the text box when window is loaded
            LicenseKeyTextBox.Focus();
            
            // Ensure title update when window loads
            UpdateWindowTitle();
        }

        private void LicenseKeyWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe event to prevent memory leaks
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
            // Focus the text box when window is activated
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LicenseKeyTextBox.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        
        private void LicenseKeyWindow_GotFocus(object sender, RoutedEventArgs e)
        {
            // Focus the text box when window gets focus
            if (!LicenseKeyTextBox.IsFocused)
            {
                LicenseKeyTextBox.Focus();
            }
        }
        
        private void LicenseKeyWindow_Deactivated(object? sender, EventArgs e)
        {
            // When window loses focus, refocus the text box when it regains focus
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
                // If icon fails to load, just continue without it
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

                // Try with the newer flag first (Windows 10 build 18985+)
                if (Win32Service.DwmSetWindowAttribute(hwnd, Win32Service.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    // If it fails, try with the old flag
                    Win32Service.DwmSetWindowAttribute(hwnd, Win32Service.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
        }

        /// <summary>
        /// Sets the status message (used by Program.cs to show connection errors)
        /// </summary>
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

            // SECURITY: Use ActivateLicenseAsync which is the ONLY method that associates HWID to the database
            // This method performs the UPDATE in the database to link the license to the machine
            var result = await _licenseService.ActivateLicenseAsync(licenseKey);

            if (result.IsValid)
            {
                // Capture license type and assign to App.LicenseType (within Dispatcher for thread safety)
                if (!string.IsNullOrEmpty(result.Tier))
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        App.LicenseType = result.Tier;
                    });
                }
                
                // Save license locally
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
                
                // Wait a bit to show success message
                await Task.Delay(1000);
                
                // Open MainWindow
                OpenMainWindow();
            }
            else
            {
                // SECURITY: If activation failed, delete local file if it exists
                // This prevents bypass even if the file was created manually
                try
                {
                    string licensePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LICENSE_FILE);
                    if (File.Exists(licensePath))
                    {
                        File.Delete(licensePath);
                    }
                }
                catch { }

                // Clear text field and show error message
                LicenseKeyTextBox.Text = "";
                UpdateStatus(result.Message, false);
                ActivateButton.IsEnabled = true;
            }
        }

        private void OpenMainWindow()
        {
            // 1. Instantiate the new main window
            var mainWindow = new MainWindow();
            
            // 2. CRITICAL: Set the new window as the application's MainWindow BEFORE closing the current one.
            // This prevents ShutdownMode.OnMainWindowClose from killing the application.
            Application.Current.MainWindow = mainWindow;
            
            // 3. Show the new window and ensure it's active
            mainWindow.Show();
            mainWindow.Activate();
            
            // 4. Now it's safe to close the license window, as it's no longer the 'MainWindow'
            this.Close();
        }

        private void UpdateStatus(string message, bool isInfo)
        {
            StatusLabel.Text = message;
            // Always use #888888 (gray) as in the Updating window
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
                // Ignore errors opening browser
            }
        }

        private void LicenseKeyTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var textBox = sender as System.Windows.Controls.TextBox;
            if (textBox != null)
            {
                CustomCaret.Visibility = string.IsNullOrEmpty(textBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            }

            // Reset status to default when user types (but keep default if no error)
            string defaultMessage = Butterfly.Services.LocalizationManager.GetString("License_PleaseEnterKey");
            if (StatusLabel.Text != "" && StatusLabel.Text != defaultMessage)
            {
                StatusLabel.Text = defaultMessage;
            }
        }

        private void LicenseKeyTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // Show custom caret when focused and text is empty
            if (string.IsNullOrEmpty(LicenseKeyTextBox.Text))
            {
                CustomCaret.Visibility = Visibility.Visible;
            }
        }

        private void LicenseKeyTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Hide custom caret when focus is lost
            CustomCaret.Visibility = Visibility.Collapsed;
            
            // Refocus the text box after a short delay
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
            // Allow activation with Enter
            if (e.Key == Key.Enter && ActivateButton.IsEnabled)
            {
                ActivateButton_Click(sender, e);
            }
        }

        private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Focus the text box when clicking on empty area
            LicenseKeyTextBox.Focus();
        }

        /// <summary>
        /// Updates the window title with the license type
        /// </summary>
        private void UpdateWindowTitle()
        {
            string fullTitle = App.GetFormattedTitle(App.LicenseType);

            // 1. Force on Window Title (WPF)
            this.Title = fullTitle;

            // 2. Force via Win32 (Windows API)
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                Butterfly.Native.Win32Service.SetWindowText(helper.Handle, fullTitle);
            }
        }
    }
}

