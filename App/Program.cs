using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Butterfly.Helpers;
using Butterfly.Services;

namespace Butterfly
{
    public partial class App : Application
    {
        /// <summary>
        /// Gets the real path of the executable directory, ignoring the temporary folder from Single File Publish
        /// </summary>
        public static string RealExecutablePath => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        /// <summary>
        /// Event fired when the license type changes
        /// </summary>
        public static event Action<string>? OnLicenseTypeChanged;
        
        private static string _licenseType = "";
        
        /// <summary>
        /// User's license type (e.g.: "Free", "Pro", "Pro+")
        /// </summary>
        public static string LicenseType 
        { 
            get => _licenseType; 
            set 
            { 
                _licenseType = value; 
                OnLicenseTypeChanged?.Invoke(value); 
            } 
        }

        /// <summary>
        /// Gets the formatted window title with the tier information.
        /// Adds rabbit emoji 🐰 only in DEBUG mode (dotnet run).
        /// </summary>
        /// <param name="tier">The license tier (e.g., "Pro", "Pro+", "Free", or null/empty)</param>
        /// <returns>The formatted title string</returns>
        public static string GetFormattedTitle(string tier)
        {
            // Base title that always appears
            string title;
            
            // Clean title if no license or Free
            if (string.IsNullOrEmpty(tier) || tier.Equals("Free", StringComparison.OrdinalIgnoreCase))
            {
                title = "Butterfly by 2014";
            }
            else
            {
                // Display tier only for Pro, Pro+, etc.
                title = $"Butterfly by 2014 ({tier})";
            }

            // Add rabbit emoji only if in Debug mode (dotnet run)
#if DEBUG
            title += " 🐰";
#endif

            return title;
        }

        public App()
        {
            // Handle unhandled exceptions in the UI thread
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            // Handle unhandled exceptions in other threads
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Log error if possible (without throwing a new exception)
            try
            {
                string errorMsg = $"Unhandled UI Exception: {e.Exception?.Message ?? "Unknown"}";
                System.Diagnostics.Debug.WriteLine(errorMsg);
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {e.Exception?.StackTrace ?? "N/A"}");
            }
            catch { }

            // Try to show message to user
            try
            {
                MessageBox.Show(
                    $"An unexpected error occurred:\n\n{e.Exception?.Message ?? "Unknown error"}\n\nThe application will continue running, but may be unstable.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            catch { }

            // Mark as handled to prevent app shutdown
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Log error if possible (without throwing a new exception)
            try
            {
                Exception? ex = e.ExceptionObject as Exception;
                string errorMsg = $"Unhandled Exception: {ex?.Message ?? "Unknown"}";
                System.Diagnostics.Debug.WriteLine(errorMsg);
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex?.StackTrace ?? "N/A"}");
                System.Diagnostics.Debug.WriteLine($"IsTerminating: {e.IsTerminating}");
            }
            catch { }

            // Try to show message to user (only if not terminating)
            if (!e.IsTerminating)
            {
                try
                {
                    Exception? ex = e.ExceptionObject as Exception;
                    MessageBox.Show(
                        $"A critical error occurred:\n\n{ex?.Message ?? "Unknown error"}\n\nThe application will try to continue.",
                        "Critical Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
                catch { }
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                // Initialize localization FIRST (before any windows are created)
                // LocalizationManager constructor loads saved preference, but SwitchLanguage may not apply
                // if Application.Current wasn't available. Ensure language is applied now.
                var localizationManager = Butterfly.Services.LocalizationManager.Instance;
                localizationManager.SwitchLanguage(localizationManager.CurrentLanguageCode);
                
                // CRITICAL: Set ShutdownMode to OnExplicitShutdown to prevent app from closing
                // when SplashWindow closes before MainWindow is shown
                this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                
                // Step 1: Verify BaseDirectory
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                {
                    string errorMsg = $"BaseDirectory is invalid or does not exist: {baseDir}";
                    MessageBox.Show(
                        $"Critical error: {errorMsg}\n\nThe application will be closed.",
                        "Initialization Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                    this.Shutdown();
                    return;
                }

                // Step 2: Configure TLS protocols (without blocking)
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol = 
                        System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
                    
                    System.Net.ServicePointManager.ServerCertificateValidationCallback += 
                        (sender, certificate, chain, sslPolicyErrors) => true;
                }
                catch
                {
                    // Continue even if TLS fails
                }
                
                // Step 3: Call base.OnStartup (CRITICAL - must be called)
                base.OnStartup(e);
                
                // Step 4: SHOW WINDOW FIRST (before any heavy processing)
                Views.SplashWindow? splashWindow = null;
                try
                {
                    splashWindow = new Views.SplashWindow();
                    splashWindow.Show();
                    
                    // Force immediate rendering
                    splashWindow.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                }
                catch (Exception ex)
                {
                    // If splash fails, try to show LicenseKeyWindow directly
                    try
                    {
                        var licenseWindow = new Views.LicenseKeyWindow();
                        licenseWindow.Show();
                        this.MainWindow = licenseWindow;
                        licenseWindow.Activate();
                        
                        // Restore normal shutdown mode (no splash transition needed)
                        this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    }
                    catch (Exception ex2)
                    {
                        MessageBox.Show(
                            $"Fatal error initializing interface:\n\n{ex.Message}\n\n{ex2.Message}\n\nThe application will be closed.",
                            "Fatal Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error
                        );
                        this.Shutdown();
                        return;
                    }
                }
                
                // Step 5: Process license in background (WITHOUT blocking the UI)
                // The window is already shown, so we can process license without rush
                const string LICENSE_FILE = "license.key";
                string licensePath = Path.Combine(baseDir, LICENSE_FILE);
                
                // Process license asynchronously, but without blocking
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500); // Give time for splash to render
                        
                        if (File.Exists(licensePath))
                        {
                            string savedKey = File.ReadAllText(licensePath).Trim();
                            
                            if (!string.IsNullOrEmpty(savedKey))
                            {
                                var licenseService = new LicenseService();
                                var result = await licenseService.ValidateLicenseAsync(savedKey);
                                
                                // Return to UI thread to update App.LicenseType (thread safety)
                                await Application.Current.Dispatcher.InvokeAsync(async () =>
                                {
                                    // Ensure App.LicenseType is updated before creating windows
                                    if (result.IsValid && !string.IsNullOrEmpty(result.Tier))
                                    {
                                        App.LicenseType = result.Tier;
                                    }
                                    
                                    Window? nextWindow = null;
                                    
                                    if (result.RequiresReactivation || !result.IsValid)
                                    {
                                        try { File.Delete(licensePath); } catch { }
                                        
                                        nextWindow = new Views.LicenseKeyWindow();
                                        ((Views.LicenseKeyWindow)nextWindow).SetStatusMessage(result.Message);
                                    }
                                    else if (result.IsValid)
                                    {
                                        nextWindow = new Views.MainWindow();
                                    }
                                    
                                    if (nextWindow != null)
                                    {
                                        // Show new window BEFORE closing splash (atomic transition)
                                        nextWindow.Show();
                                        this.MainWindow = nextWindow;
                                        nextWindow.Activate();
                                        
                                        // Close splash only after new window is shown
                                        try
                                        {
                                            if (splashWindow != null)
                                            {
                                                splashWindow.FadeOutAndClose();
                                                await Task.Delay(500);
                                            }
                                        }
                                        catch { }
                                        
                                        // Restore normal shutdown mode after successful transition
                                        this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                                    }
                                });
                                return;
                            }
                        }
                        
                        // If license does not exist or is empty
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            // Create and show new window BEFORE closing splash (atomic transition)
                            var licenseWindow = new Views.LicenseKeyWindow();
                            licenseWindow.Show();
                            this.MainWindow = licenseWindow;
                            licenseWindow.Activate();
                            
                            // Close splash only after new window is shown
                            try
                            {
                                if (splashWindow != null)
                                {
                                    splashWindow.FadeOutAndClose();
                                    await Task.Delay(500);
                                }
                            }
                            catch { }
                            
                            // Restore normal shutdown mode after successful transition
                            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        });
                    }
                    catch
                    {
                        // In case of error, show LicenseKeyWindow
                        try
                        {
                            await Application.Current.Dispatcher.InvokeAsync(async () =>
                            {
                                try { if (File.Exists(licensePath)) File.Delete(licensePath); } catch { }
                                
                                // Create and show new window BEFORE closing splash (atomic transition)
                                var licenseWindow = new Views.LicenseKeyWindow();
                                licenseWindow.Show();
                                this.MainWindow = licenseWindow;
                                licenseWindow.Activate();
                                
                                // Close splash only after new window is shown
                                try
                                {
                                    if (splashWindow != null)
                                    {
                                        splashWindow.FadeOutAndClose();
                                        await Task.Delay(500);
                                    }
                                }
                                catch { }
                                
                                // Restore normal shutdown mode after successful transition
                                this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                            });
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                // Last line of defense - catch any unhandled exception in OnStartup
                
                try
                {
                    MessageBox.Show(
                        $"Error starting the application:\n\n{ex.Message}\n\nThe application will be closed.",
                        "Initialization Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
                catch
                {
                    // If even MessageBox fails, at least log
                    System.Diagnostics.Debug.WriteLine("Fatal error during startup - unable to show message");
                }
                
                // Shutdown application if there is a critical error during initialization
                this.Shutdown();
            }
        }
    }
}
