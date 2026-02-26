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
        public static string RealExecutablePath => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        public static event Action<string>? OnLicenseTypeChanged;
        
        private static string _licenseType = "";
        
        public static string LicenseType 
        { 
            get => _licenseType; 
            set 
            { 
                _licenseType = value; 
                OnLicenseTypeChanged?.Invoke(value); 
            } 
        }

        public static string GetFormattedTitle(string tier)
        {
            string title;
            
            if (string.IsNullOrEmpty(tier) || tier.Equals("Free", StringComparison.OrdinalIgnoreCase))
            {
                title = "Butterfly";
            }
            else
            {
                title = $"Butterfly ({tier})";
            }

#if DEBUG
            title += " 🐰";
#endif

            return title;
        }

        public App()
        {
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                string errorMsg = $"Unhandled UI Exception: {e.Exception?.Message ?? "Unknown"}";
                System.Diagnostics.Debug.WriteLine(errorMsg);
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {e.Exception?.StackTrace ?? "N/A"}");
            }
            catch { }

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

            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception? ex = e.ExceptionObject as Exception;
                string errorMsg = $"Unhandled Exception: {ex?.Message ?? "Unknown"}";
                System.Diagnostics.Debug.WriteLine(errorMsg);
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex?.StackTrace ?? "N/A"}");
                System.Diagnostics.Debug.WriteLine($"IsTerminating: {e.IsTerminating}");
            }
            catch { }

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
                var localizationManager = Butterfly.Services.LocalizationManager.Instance;
                localizationManager.SwitchLanguage(localizationManager.CurrentLanguageCode);
                
                this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                
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

                try
                {
                    System.Net.ServicePointManager.SecurityProtocol = 
                        System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
                    
                    System.Net.ServicePointManager.ServerCertificateValidationCallback += 
                        (sender, certificate, chain, sslPolicyErrors) => true;
                }
                catch
                {
                    // continue even if TLS fails
                }
                
                base.OnStartup(e);
                
                Views.SplashWindow? splashWindow = null;
                try
                {
                    splashWindow = new Views.SplashWindow();
                    splashWindow.Show();
                    
                    splashWindow.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                }
                catch (Exception ex)
                {
                    try
                    {
                        var licenseWindow = new Views.LicenseKeyWindow();
                        licenseWindow.Show();
                        this.MainWindow = licenseWindow;
                        licenseWindow.Activate();
                        
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
                
                const string LICENSE_FILE = "license.key";
                string licensePath = Path.Combine(baseDir, LICENSE_FILE);
                
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);
                        
                        if (File.Exists(licensePath))
                        {
                            string savedKey = File.ReadAllText(licensePath).Trim();
                            
                            if (!string.IsNullOrEmpty(savedKey))
                            {
                                var licenseService = new LicenseService();
                                var result = await licenseService.ValidateLicenseAsync(savedKey);
                                
                                await Application.Current.Dispatcher.InvokeAsync(async () =>
                                {
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
                                        nextWindow.Show();
                                        this.MainWindow = nextWindow;
                                        nextWindow.Activate();
                                        
                                        try
                                        {
                                            if (splashWindow != null)
                                            {
                                                splashWindow.FadeOutAndClose();
                                                await Task.Delay(500);
                                            }
                                        }
                                        catch { }
                                        
                                        this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                                    }
                                });
                                return;
                            }
                        }
                        
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            var licenseWindow = new Views.LicenseKeyWindow();
                            licenseWindow.Show();
                            this.MainWindow = licenseWindow;
                            licenseWindow.Activate();
                            
                            try
                            {
                                if (splashWindow != null)
                                {
                                    splashWindow.FadeOutAndClose();
                                    await Task.Delay(500);
                                }
                            }
                            catch { }
                            
                            this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        });
                    }
                    catch
                    {
                        try
                        {
                            await Application.Current.Dispatcher.InvokeAsync(async () =>
                            {
                                try { if (File.Exists(licensePath)) File.Delete(licensePath); } catch { }
                                
                                var licenseWindow = new Views.LicenseKeyWindow();
                                licenseWindow.Show();
                                this.MainWindow = licenseWindow;
                                licenseWindow.Activate();
                                
                                try
                                {
                                    if (splashWindow != null)
                                    {
                                        splashWindow.FadeOutAndClose();
                                        await Task.Delay(500);
                                    }
                                }
                                catch { }
                                
                                this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                            });
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {                
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
                    System.Diagnostics.Debug.WriteLine("Fatal error during startup - unable to show message");
                }
                
                this.Shutdown();
            }
        }
    }
}
