using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Butterfly.Native;
using Butterfly.Models;
using Butterfly.Services;
using Butterfly.ViewModels;
using Butterfly.Helpers;
using static Butterfly.Native.Win32Service;

namespace Butterfly.Views
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly GameApiService gameApiService;
        private readonly UpdateService updateService;
        private readonly string autoSaveFilePath;
        private readonly AccountDataService accountDataService;
        private readonly AccountStatsService accountStatsService;
        private readonly MainViewModel viewModel;
        private SemaphoreSlim checkStatusSemaphore = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _reconnectionSemaphore = new SemaphoreSlim(1, 1);
        
        public const string Version = "1.0.8";
        
        private string GameFolderName
        {
            get
            {
                if (selectedServer == null)
                {
                    return "MixMaster Source";
                }
                return selectedServer.Name;
            }
        }
        
        private Account? draggedItem = null;
        private Server? selectedServer = null;
        private bool isInitialLoadComplete = false;
        private CancellationTokenSource? autoRefreshCancellationTokenSource;
        private readonly object autoRefreshLock = new object();
        private bool _isServerRequestInProgress = false;
        private ConsoleWindow? consoleWindow;
        private bool isPlaying = false;
        private volatile bool isConnecting = false;
        private bool _gameWindowsVisible = true;
        
        public bool IsGameVisible
        {
            get => _gameWindowsVisible;
            set
            {
                if (_gameWindowsVisible != value)
                {
                    _gameWindowsVisible = value;
                    OnPropertyChanged(nameof(IsGameVisible));
                }
            }
        }

        private readonly LoggerHelper loggerHelper;

        private RankingCache? rankingCache = null;

        public MainWindow()
        {
            Win32Service.SetProcessDPIAware();
            
            InitializeComponent();
            
            this.DataContext = this;
            
            UpdateFabTooltipState();

            if (!SecurityHelper.IsRunningAsAdministrator())
            {
                MessageBox.Show(
                    Butterfly.Services.LocalizationManager.GetString("Msg_AdminRequired"),
                    Butterfly.Services.LocalizationManager.GetString("Msg_AdminRequiredTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }

            loggerHelper = new LoggerHelper();

            string dataFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
            if (!Directory.Exists(dataFolder))
            {
                var directoryInfo = Directory.CreateDirectory(dataFolder);
                if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                {
                    File.SetAttributes(dataFolder, directoryInfo.Attributes | FileAttributes.Hidden);
                }
            }
            autoSaveFilePath = Path.Combine(dataFolder, "accounts.dat");

            accountDataService = new AccountDataService(autoSaveFilePath);
            accountStatsService = new AccountStatsService();
            gameApiService = new GameApiService();
            updateService = new UpdateService();

            viewModel = new MainViewModel();
            this.DataContext = viewModel;
            
            if (viewModel.SelectedLanguage != null)
            {
                Butterfly.Services.LocalizationManager.Instance.SwitchLanguage(viewModel.SelectedLanguage.Code);
            }

            consoleWindow = new ConsoleWindow(this); // Pass MainWindow reference
            consoleWindow.Closing += ConsoleWindow_Closing;

            var uiDispatcher = Application.Current.Dispatcher;
            gameApiService.OnLogRequested = (msg, type) => uiDispatcher.Invoke(() => LogToConsole(msg, type));
            updateService.OnLogRequested = (msg, type) => uiDispatcher.Invoke(() => LogToConsole(msg, type));

            ApplyDarkTitleBar();

            isInitialLoadComplete = true;
            SetContextMenusEnabled(true);

            this.Closing += MainWindow_Closing;
            
            this.Loaded += MainWindow_Loaded;
            
            this.Unloaded += MainWindow_Unloaded;
            
            App.OnLicenseTypeChanged += MainWindow_OnLicenseTypeChanged;
            
            UpdateWindowTitle();
        }

        public async void Play()
        {
            if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotStartMonitoring"), "INFO");
                return;
            }
            
            if (selectedServer == null && viewModel.Servers.Any())
            {
                var firstServer = viewModel.Servers.First();
                ServersGrid.SelectedItem = firstServer;
            }
            
            if (isPlaying) return;
            
            isPlaying = true;
            
            consoleWindow?.UpdatePlayPauseState(true);
            UpdatePlayPauseIcon();
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_StartingMonitoring"), "INFO");
            
            isConnecting = false;
            
            if (selectedServer != null)
            {
                selectedServer.Status = "Checking...";
            }
            
            foreach (var account in viewModel.Accounts)
            {
                if (account.Status == "Online" || account.Status == "Offline")
                {
                    account.Status = "Checking...";
                }
            }
            
            await Task.Delay(100);
            
            StartAutoRefreshTimer();
        }

        public void Pause()
        {
            if (!isPlaying) return;
            
            isPlaying = false;
            
            consoleWindow?.UpdatePlayPauseState(false);
            UpdatePlayPauseIcon();
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_MonitoringPaused"), "INFO");
            
            autoRefreshCancellationTokenSource?.Cancel();
            autoRefreshCancellationTokenSource?.Dispose();
            autoRefreshCancellationTokenSource = null;
            
            if (selectedServer != null)
            {
                selectedServer.IsSelected = false;
                selectedServer = null;
            }
            ServersGrid.SelectedItem = null;
            viewModel.IsServerSelected = false;
            
            foreach (var account in viewModel.Accounts)
            {
                account.Status = "Idle";
                account.Level = string.Empty;
                account.Experience = string.Empty;
            }
        }

        public AccountStats? GetAccountStats()
        {
            return accountStatsService.GetAccountStats(viewModel.Accounts);
        }

        public bool ReconnectCharacter(string characterName)
        {
            try
            {
                var account = viewModel.Accounts.FirstOrDefault(a => 
                    a.Character.Equals(characterName, StringComparison.OrdinalIgnoreCase));
                
                if (account == null)
                {
                    return false;
                }

                _ = Task.Run(async () => await ActivateBotForAccount(account));
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string GetLogsFolderPath()
        {
            return LoggerHelper.GetLogsFolderPath();
        }

        public void ToggleGameWindows()
        {
            ExecuteToggleGameWindowsCommand();
        }

        public System.Collections.Generic.List<Account> GetAllAccounts()
        {
            try
            {
                return viewModel.Accounts.ToList();
            }
            catch
            {
                return new System.Collections.Generic.List<Account>();
            }
        }

        public async void RefreshOnce()
        {
            if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotRefresh"), "INFO");
                return;
            }
            
            if (selectedServer == null)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotRefreshNoServer"), "INFO");
                return;
            }

            bool connecting = false;
            await Dispatcher.InvokeAsync(() =>
            {
                connecting = isConnecting;
            });
            
            if (connecting)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_RefreshSkipped"), "INFO");
                return;
            }
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_RefreshingOnce"), "INFO");
            
            selectedServer.Status = "Checking...";
            
            foreach (var account in viewModel.Accounts)
            {
                if (account.Status == "Online" || account.Status == "Offline")
                {
                    account.Status = "Checking...";
                }
            }
            
            await Task.Delay(100);
            
            await CheckAllServersAndAccountsStatus();
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_RefreshComplete"), "INFO");
        }

        private void ConsoleWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            ConsoleControlsBar.Visibility = Visibility.Visible;
        }

        private void TerminalButton_Click(object sender, RoutedEventArgs e)
        {
            if (consoleWindow == null) return;

            if (consoleWindow.IsVisible)
            {
                consoleWindow.Hide();
                ConsoleControlsBar.Visibility = Visibility.Visible;
            }
            else
            {
                consoleWindow.Show();
                ConsoleControlsBar.Visibility = Visibility.Collapsed;
            }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
                return;
            
            if (isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
            UpdatePlayPauseIcon();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshOnce();
        }

        private void UpdateFabTooltipState()
        {
            string tooltipText = IsGameVisible 
                ? Butterfly.Services.LocalizationManager.GetString("ToolTip_HideGameWindows")
                : Butterfly.Services.LocalizationManager.GetString("ToolTip_ShowGameWindows");

            ToolTip tt;
            if (!(DisplayFAB.ToolTip is ToolTip existingToolTip))
            {
                tt = new ToolTip();
                tt.Style = (Style)FindResource("FloatingButtonToolTipStyle");
                DisplayFAB.ToolTip = tt;
            }
            else
            {
                tt = existingToolTip;
            }
            
            tt.IsHitTestVisible = false;
            tt.VerticalContentAlignment = VerticalAlignment.Center;
            tt.Content = tooltipText;
        }

        private void DisplayFAB_MouseEnter(object sender, MouseEventArgs e)
        {
            UpdateFabTooltipState();
            if (DisplayFAB.ToolTip is ToolTip tt)
            {
                tt.IsOpen = true;
            }
        }

        private void DisplayFAB_MouseLeave(object sender, MouseEventArgs e)
        {
            if (DisplayFAB.ToolTip is ToolTip tt)
            {
                tt.IsOpen = false;
            }
        }

        private void DisplayFAB_Click(object sender, RoutedEventArgs e)
        {
            ExecuteToggleGameWindowsCommand();
            UpdateFabTooltipState();
            
            if (DisplayFAB.ToolTip is ToolTip tt)
            {
                tt.IsOpen = false;
                tt.IsOpen = true;
            }
            
            if (_gameWindowsVisible)
            {
                this.Topmost = true;
                this.Activate();
                this.UpdateLayout();

                Dispatcher.BeginInvoke(new Action(() => 
                { 
                    this.Topmost = false; 
                    this.Focus(); 
                }), DispatcherPriority.Render);
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                
                RefreshOnce();
            }
        }

        private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            if (consoleWindow != null && consoleWindow.IsVisible)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var commandText = textBox.Text.Trim();
                var command = commandText.ToLower();

                if (!string.IsNullOrEmpty(command))
                {
                    LogToConsole($"> {commandText}", "INFO");

                    ProcessConsoleCommand(command, commandText);

                    textBox.Text = "";
                    textBox.Focus();
                }
            }
        }

        private void ConsoleInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            // method kept for compatibility
        }

        private void ProcessConsoleCommand(string command, string originalCommand)
        {
            var parts = originalCommand.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var commandName = parts[0].ToLower();
            var arguments = parts.Length > 1 ? parts[1] : string.Empty;

            var commandHandlers = new Dictionary<string, Action<string>>
            {
                { "cls", (args) => { consoleWindow?.ClearLog(); LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConsoleCleared"), "INFO"); } },
                { "clear", (args) => { consoleWindow?.ClearLog(); LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConsoleCleared"), "INFO"); } },
                { "help", (args) => ExecuteHelpCommand() },
                { "logs", (args) => ExecuteLogsCommand() },
                { "display", (args) => ExecuteToggleGameWindowsCommand() },
                { "exit", (args) => ExecuteExitCommand() }
            };

            if (commandHandlers.TryGetValue(commandName, out var handler))
            {
                handler(arguments);
            }
            else
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_UnknownCommand", originalCommand), "INFO");
            }
        }

        private void ExecuteHelpCommand()
        {
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AvailableCommands"), "INFO");
            LogToConsole("  " + Butterfly.Services.LocalizationManager.GetString("Log_CmdCls"), "INFO");
            LogToConsole("  " + Butterfly.Services.LocalizationManager.GetString("Log_CmdStats"), "INFO");
            LogToConsole("  " + Butterfly.Services.LocalizationManager.GetString("Log_CmdLogs"), "INFO");
            LogToConsole("  " + Butterfly.Services.LocalizationManager.GetString("Log_CmdDisplay"), "INFO");
            LogToConsole("  " + Butterfly.Services.LocalizationManager.GetString("Log_CmdHelp"), "INFO");
            LogToConsole("  " + Butterfly.Services.LocalizationManager.GetString("Log_CmdExit"), "INFO");
        }

        private void ExecuteToggleGameWindowsCommand()
        {
            try
            {
                Process[] mixMasterProcesses = Process.GetProcessesByName("MixMaster");
                
                if (mixMasterProcesses.Length == 0)
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_NoMixMasterWindow"), "INFO");
                    return;
                }

                int totalWindowsAffected = 0;
                
                bool show = !_gameWindowsVisible;
                
                foreach (Process process in mixMasterProcesses)
                {
                    try
                    {
                        int windowsAffected = Win32Service.ToggleGameWindows(process, show);
                        totalWindowsAffected += windowsAffected;
                    }
                    catch (Exception ex)
                    {
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorTogglingWindows", process.Id, ex.Message), "ERROR");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                IsGameVisible = !_gameWindowsVisible;

                if (IsGameVisible)
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WindowsVisible"), "INFO");
                }
                else
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WindowsHidden"), "INFO");
                }
            }
            catch (Exception ex)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorToggleWindows", ex.Message), "ERROR");
            }
        }

        private void ExecuteLogsCommand()
        {
            var logsPath = GetLogsFolderPath();
            if (string.IsNullOrEmpty(logsPath))
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorLogsFolder"), "INFO");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", logsPath);
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_OpenedLogsFolder", logsPath), "INFO");
            }
            catch (Exception ex)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorOpeningLogs", ex.Message), "INFO");
            }
        }

        private void ExecuteExitCommand()
        {
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ShuttingDown"), "INFO");
            Application.Current.Shutdown();
        }

        private void UpdatePlayPauseIcon()
        {
            if (PlayIcon == null || PauseIcon == null) return;

            var duration = TimeSpan.FromMilliseconds(200);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

            if (isPlaying)
            {
                var playFadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                var pauseFadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };

                PlayIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, playFadeOut);
                PauseIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, pauseFadeIn);
            }
            else
            {
                var pauseFadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                var playFadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };

                PauseIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, pauseFadeOut);
                PlayIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, playFadeIn);
            }
        }

        private void StartAutoRefreshTimer()
        {
            lock (autoRefreshLock)
            {
                try
                {
                    autoRefreshCancellationTokenSource?.Cancel();
                }
                catch
                {
                    // ignore errors when canceling old token
                }
                
                try
                {
                    autoRefreshCancellationTokenSource?.Dispose();
                }
                catch
                {
                    // ignore errors when disposing old token
                }
                
                autoRefreshCancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = autoRefreshCancellationTokenSource.Token;
                
                _ = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested && isPlaying)
                    {
                        try
                        {
                            bool connecting = false;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                connecting = isConnecting;
                            });
                            
                            if (connecting)
                            {
                                await Task.Delay(15000, cancellationToken);
                                continue;
                            }
                            
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (selectedServer != null)
                                {
                                    selectedServer.Status = "Checking...";
                                }
                                
                                foreach (var account in viewModel.Accounts)
                                {
                                    if (account.Status == "Online" || account.Status == "Offline")
                                    {
                                        account.Status = "Checking...";
                                    }
                                }
                            });
                            
                            _isServerRequestInProgress = true;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (ServersGrid != null)
                                {
                                    ServersGrid.IsEnabled = false;
                                }
                            });
                            
                            try
                            {
                                await CheckAllServersAndAccountsStatus();
                            }
                            finally
                            {
                                _isServerRequestInProgress = false;
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (ServersGrid != null)
                                    {
                                        ServersGrid.IsEnabled = true;
                                    }
                                });
                            }
                            
                            await Task.Delay(15000, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorMonitoringLoop", ex.Message), "ERROR");
                                });
                            }
                            catch
                            {
                                // if unable to log, continue silently
                            }
                            
                            // Garantir que o ServersGrid seja reabilitado em caso de erro
                            _isServerRequestInProgress = false;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (ServersGrid != null)
                                {
                                    ServersGrid.IsEnabled = true;
                                }
                            });
                            
                            try
                            {
                                await Task.Delay(15000, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }
                }, cancellationToken);
            }
        }

        private void SetContextMenusEnabled(bool enabled)
        {
            Dispatcher.Invoke(() =>
            {
                if (this.FindName("MainBorder") is Border mainBorder && mainBorder.ContextMenu != null)
                {
                    mainBorder.ContextMenu.IsEnabled = enabled;
                }

                AccountsGrid.ContextMenu = enabled ? AccountsGrid.ContextMenu : null;
            });
        }

        private void InitializeServers()
        {
            viewModel.Servers.Add(new Server
            {
                Name = "MixMaster Source",
                Url = "https://mixmastersource.com/",
                RankingUrl = "https://mixmastersource.com/RankingHero?p=1&=1&count=500",
                Status = "Idle",
                OnlinePlayers = 0
            });

            viewModel.Servers.Add(new Server
            {
                Name = "MixMaster Adventure",
                Url = "https://mixmasteradventure.com/",
                RankingUrl = "https://mixmasteradventure.com/api/v1/rankings/hero?limit=1000",
                Status = "Idle",
                OnlinePlayers = 0
            });

            viewModel.Servers.Add(new Server
            {
                Name = "MixMaster Origin",
                Url = "http://31.97.91.174/",
                RankingUrl = "http://31.97.91.174/ranking",
                Status = "Idle",
                OnlinePlayers = 0
            });
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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkTitleBar();
        }

        private void PositionConsoleWindow()
        {
            if (consoleWindow != null)
            {
                consoleWindow.Left = this.Left + this.Width + 10;
                consoleWindow.Top = this.Top;
                consoleWindow.Width = this.Width;
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
        }

        private void LoadAccountsAutomatically()
        {
            var loadedAccounts = accountDataService.LoadAccounts();
            foreach (var account in loadedAccounts)
            {
                viewModel.Accounts.Add(account);
            }
        }

        private void SaveAccountsAutomatically()
        {
            accountDataService.SaveAccounts(viewModel.Accounts);
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            autoRefreshCancellationTokenSource?.Cancel();
            autoRefreshCancellationTokenSource?.Dispose();
            autoRefreshCancellationTokenSource = null;

            if (consoleWindow != null)
            {
                consoleWindow.Close();
            }

            SaveAccountsAutomatically();
            
            loggerHelper?.Dispose();
        }

        private void ShowUpdateOverlay()
        {
            Dispatcher.Invoke(() =>
            {
                if (UpdateOverlay != null)
                {
                    UpdateOverlay.Visibility = Visibility.Visible;
                    UpdateStatusText.Text = Butterfly.Services.LocalizationManager.GetString("Status_InitializingUpdate");
                    LoadUpdateOverlayIcon();
                }
            });
        }

        private void LoadUpdateOverlayIcon()
        {
            try
            {
                if (UpdateOverlayIcon == null) return;
                
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Assets", "butterfly.ico");
                if (File.Exists(iconPath))
                {
                    using (System.Drawing.Icon icon = new System.Drawing.Icon(iconPath))
                    {
                        BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        UpdateOverlayIcon.Source = bitmapSource;
                    }
                }
            }
            catch
            {
                // if icon fails to load, just continue without it
            }
        }

        private void UpdateOverlayStatus(string cleanMessage)
        {
            Dispatcher.Invoke(() =>
            {
                if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
                {
                    if (cleanMessage.Contains("bytes") || cleanMessage.Contains("Bytes") ||
                        cleanMessage.Contains("File size") || cleanMessage.Contains("Directory does not exist") ||
                        cleanMessage.Contains("Permission denied") || cleanMessage.Contains("Directory not found") ||
                        cleanMessage.Contains("File I/O error") || cleanMessage.Contains("Network error") ||
                        cleanMessage.Contains("Download timeout") || cleanMessage.Contains("TIP:") ||
                        System.Text.RegularExpressions.Regex.IsMatch(cleanMessage, @"\d+\s*(bytes|Bytes|MB|KB|GB)"))
                    {
                        return;
                    }
                    
                    UpdateStatusText.Text = cleanMessage;
                }
            });
        }

        private void LogToConsole(string message, string level = "INFO")
        {
            if (consoleWindow == null) return;

            string formattedMessage = LoggerHelper.FormatConsoleMessage(message, level);
            string newLine = formattedMessage + "\n";

            consoleWindow.AppendLog(newLine);
            
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (txtLastLog != null)
                {
                    string displayText = System.Text.RegularExpressions.Regex.Replace(
                        formattedMessage, 
                        @"[\d/]+\s+\d{2}:\d{2}:\d{2}\s+", 
                        string.Empty
                    );
                    
                    var defaultColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                    
                    txtLastLog.Text = displayText;
                    txtLastLog.Foreground = defaultColor;
                }
                
                if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
                {
                    if (message.Contains("Download") || message.Contains("update") || message.Contains("Update") || 
                        message.Contains("complete") || message.Contains("Preparing") || message.Contains("failed"))
                    {
                        UpdateOverlayStatus(message);
                    }
                }
            }));
            
            loggerHelper.WriteToLogFile(message, level);
        }

        private void ClearConsole_Click(object sender, RoutedEventArgs e)
        {
            consoleWindow?.ClearLog();
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConsoleCleared"), "INFO");
        }

        private async Task<RankingCache?> FetchRankingData(string rankingUrl, string? serverName = null)
        {
            try
            {
                if (isConnecting)
                {
                    return null;
                }

                var cache = await gameApiService.FetchRankingDataAsync(rankingUrl, serverName);
                
                return cache;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool?> CheckCharacterOnline(string characterName, bool forceRefresh = false)
        {
            if (forceRefresh)
            {
                rankingCache = null;
            }

            if (rankingCache != null && 
                (DateTime.Now - rankingCache.LastUpdate).TotalSeconds < 60)
            {
                if (rankingCache.CharacterStatus.ContainsKey(characterName))
                {
                    return rankingCache.CharacterStatus[characterName];
                }
                else
                {
                    return null;
                }
            }

            if (selectedServer != null && !string.IsNullOrEmpty(selectedServer.RankingUrl))
            {
                var cache = await FetchRankingData(selectedServer.RankingUrl, selectedServer.Name);
                if (cache != null)
                {
                    rankingCache = cache;
                    
                    if (cache.CharacterStatus.TryGetValue(characterName, out bool isOnline))
                    {
                        return isOnline;
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        private async Task CheckServerStatus(Server server)
        {
            if (string.IsNullOrEmpty(server.RankingUrl))
            {
                server.Status = "Offline";
                server.OnlinePlayers = 0;
                return;
            }

            try
            {
                var cache = await FetchRankingData(server.RankingUrl, server.Name);
                if (cache != null)
                {
                    server.Status = "Online";
                    server.OnlinePlayers = cache.OnlineCount;

                    if (server == selectedServer)
                    {
                        rankingCache = cache;
                    }
                }
                else
                {
                    server.Status = "Offline";
                    server.OnlinePlayers = 0;
                }
            }
            catch
            {
                server.Status = "Offline";
                server.OnlinePlayers = 0;
            }
        }

        private async Task CheckAllServersAndAccountsStatus()
        {
            if (isConnecting)
            {
                return;
            }
            
            await checkStatusSemaphore.WaitAsync();
            try
            {
                if (selectedServer != null && !string.IsNullOrEmpty(selectedServer.RankingUrl))
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CheckingServerStatus", selectedServer.Name), "REQUEST");
                    
                    await CheckServerStatus(selectedServer);
                }

                var accountsCopy = viewModel.Accounts.ToList();
                foreach (var account in accountsCopy)
                {
                    if (rankingCache != null && rankingCache.CharacterStatus.ContainsKey(account.Character))
                    {
                        bool isOnline = rankingCache.CharacterStatus[account.Character];
                        account.Status = isOnline ? "Online" : "Offline";
                        
                        if (rankingCache.CharacterLevel.TryGetValue(account.Character, out string? level))
                        {
                            account.Level = level;
                        }
                        else
                        {
                            account.Level = string.Empty;
                        }
                        
                        if (rankingCache.CharacterExperience.TryGetValue(account.Character, out string? exp))
                        {
                            account.Experience = exp;
                        }
                        else
                        {
                            account.Experience = string.Empty;
                        }
                    }
                    else
                    {
                        account.Status = "Idle";
                        account.Level = string.Empty;
                        account.Experience = string.Empty;
                    }
                }
            }
            finally
            {
                checkStatusSemaphore.Release();
            }
            
            await CheckAndStartReconnectionIfNeeded();
        }
        
        private async Task CheckAndStartReconnectionIfNeeded()
        {
            var accountsCopy = await Dispatcher.InvokeAsync(() => viewModel.Accounts.ToList());
            
            foreach (var account in accountsCopy)
            {
                string accountStatus = string.Empty;
                bool isBotActive = false;
                bool isReconnecting = false;
                int gameProcessId = 0;
                string characterName = string.Empty;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    accountStatus = account.Status;
                    isBotActive = account.IsBotActive;
                    isReconnecting = account.IsReconnecting;
                    gameProcessId = account.GameProcessId;
                    characterName = account.Character;
                });
                
                if (accountStatus == "Offline" && isBotActive && !isReconnecting)
                {
                    if (gameProcessId > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ExitingGameWindow"), characterName);
                        });
                        
                        await ForceKillGameProcess(account, gameProcessId);
                        
                        await Dispatcher.InvokeAsync(() =>
                        {
                            account.GameProcessId = 0;
                        });
                        
                        await Task.Delay(5000);
                    }
                    
                    _ = Task.Run(() => AutoReconnectLoop(account));
                }
            }
        }

        private async Task ForceKillGameProcess(Account account, int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                process.Refresh();
                
                if (!process.HasExited)
                {
                    process.Kill();
                    await Task.Delay(500);
                }
            }
            catch (ArgumentException)
            {
                // process no longer exists - that's fine, continue
            }
            catch (System.ComponentModel.Win32Exception)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await Task.Delay(500);
                    }
                }
                catch
                {
                    // if still fails, continue silently
                }
            }
            catch
            {
                // any other error - continue silently
            }
        }

        private async Task CheckAllAccountsStatus(bool forceRefresh = false)
        {
            await checkStatusSemaphore.WaitAsync();
            try
            {
                if (forceRefresh)
                {
                    rankingCache = null;
                    foreach (var account in viewModel.Accounts)
                    {
                        if (account.Status == "Online" || account.Status == "Offline")
                        {
                            account.Status = "Checking...";
                            account.Level = string.Empty;
                            account.Experience = string.Empty;
                        }
                    }
                }

                if (selectedServer != null && !string.IsNullOrEmpty(selectedServer.RankingUrl))
                {
                    if (rankingCache == null || (DateTime.Now - rankingCache.LastUpdate).TotalSeconds > 60)
                    {
                        var cache = await FetchRankingData(selectedServer.RankingUrl, selectedServer.Name);
                        if (cache != null)
                        {
                            rankingCache = cache;
                            selectedServer.OnlinePlayers = cache.OnlineCount;
                        }
                    }
                }

                var accountsCopy = viewModel.Accounts.ToList();
                foreach (var account in accountsCopy)
                {
                    if (rankingCache != null && rankingCache.CharacterStatus.ContainsKey(account.Character))
                    {
                        bool isOnline = rankingCache.CharacterStatus[account.Character];
                        account.Status = isOnline ? "Online" : "Offline";
                        
                        if (rankingCache.CharacterLevel.TryGetValue(account.Character, out string? level))
                        {
                            account.Level = level;
                        }
                        else
                        {
                            account.Level = string.Empty;
                        }
                        
                        if (rankingCache.CharacterExperience.TryGetValue(account.Character, out string? exp))
                        {
                            account.Experience = exp;
                        }
                        else
                        {
                            account.Experience = string.Empty;
                        }
                    }
                    else
                    {
                        account.Status = "Idle";
                        account.Level = string.Empty;
                        account.Experience = string.Empty;
                    }
                }
            }
            finally
            {
                checkStatusSemaphore.Release();
            }
            
            await CheckAndStartReconnectionIfNeeded();
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddAccountDialog();
            if (dialog.ShowDialog() == true)
            {
                var newAccount = new Account
                {
                    Username = dialog.AccountUsername,
                    Password = dialog.AccountPassword,
                    Character = dialog.AccountCharacter,
                    Status = isPlaying ? "Checking..." : "Paused"
                };

                viewModel.Accounts.Add(newAccount);

                if (isPlaying)
                {
                    _ = Task.Run(async () =>
                    {
                        bool? isOnline = await CheckCharacterOnline(newAccount.Character);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            newAccount.Status = isOnline == null ? "Idle" : (isOnline.Value ? "Online" : "Offline");
                        });
                    });
                }
            }
        }

        private void AccountsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // can be used for future actions when an account is selected
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            AccountsGrid.UnselectAll();
        }

        private void ToggleUsernameVisibility_Click(object sender, RoutedEventArgs e)
        {
            viewModel.IsUsernameVisible = !viewModel.IsUsernameVisible;
        }

        private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
        {
            viewModel.IsPasswordVisible = !viewModel.IsPasswordVisible;
        }

        private void LanguageSelectorButton_Click(object sender, RoutedEventArgs e)
        {
            var popup = LanguageDropdownPopup;
            if (popup != null)
            {
                popup.IsOpen = !popup.IsOpen;
            }
        }

        private void LanguageItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.Tag is Language selectedLanguage)
            {
                viewModel.SelectedLanguage = selectedLanguage;
                LanguageDropdownPopup.IsOpen = false;
            }
        }

        private void RemoveAccount_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                var account = menuItem.Tag as Account;

                if (account == null)
                {
                    var contextMenu = menuItem.Parent as ContextMenu;
                    account = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as Account;
                }

                if (account != null)
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterRemoved", account.Character), "INFO");
                    viewModel.Accounts.Remove(account);
                }
            }
        }

        private async void RefreshAccount_Click(object sender, RoutedEventArgs e)
        {
            if (selectedServer == null)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotRefreshNoServer"), "INFO");
                return;
            }

            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                var account = menuItem.Tag as Account;

                if (account == null)
                {
                    var contextMenu = menuItem.Parent as ContextMenu;
                    account = (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext as Account;
                }

                if (account != null)
                {
                    await RefreshIndividualAccount(account);
                }
            }
        }
        
        private async Task<bool> RefreshIndividualAccount(Account account)
        {
            if (selectedServer == null)
            {
                return false;
            }

            if (isConnecting)
            {
                return false;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (account.Status == "Online" || account.Status == "Offline")
                {
                    account.Status = "Checking...";
                }
            });
            
            bool? isOnline = await CheckCharacterOnline(account.Character, forceRefresh: true);
            
            await Dispatcher.InvokeAsync(() =>
            {
                account.Status = isOnline == null ? "Idle" : (isOnline.Value ? "Online" : "Offline");
            });
            
            bool shouldReconnect = false;
            await Dispatcher.InvokeAsync(() =>
            {
                shouldReconnect = account.Status == "Offline" 
                               && account.IsBotActive 
                               && account.GameProcessId == 0
                               && !account.IsReconnecting;
            });
            
            if (shouldReconnect)
            {
                _ = Task.Run(() => AutoReconnectLoop(account));
            }
            
            return isOnline == true;
        }

        private void RefreshAll_Click(object sender, RoutedEventArgs e)
        {
            RefreshOnce();
        }

        private void SaveAccountsToFile_Click(object sender, RoutedEventArgs e)
        {
            if (!isInitialLoadComplete)
                return;

            try
            {
                var accsFolder = Path.Combine(App.RealExecutablePath, ".Butterfly", "accs");
                if (!Directory.Exists(accsFolder))
                {
                    var directoryInfo = Directory.CreateDirectory(accsFolder);
                    string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                    if (Directory.Exists(butterflyFolder))
                    {
                        var butterflyDirInfo = new DirectoryInfo(butterflyFolder);
                        if ((butterflyDirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            File.SetAttributes(butterflyFolder, butterflyDirInfo.Attributes | FileAttributes.Hidden);
                        }
                    }
                }

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save Accounts",
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    DefaultExt = "txt",
                    FileName = DateTime.Now.ToString("MMddyyyyHHmmss") + ".txt",
                    InitialDirectory = accsFolder
                };

                        if (saveDialog.ShowDialog() == true)
                        {
                            var lines = viewModel.Accounts.Select(a => $"{a.Character},{a.Username},{a.Password}");
                            File.WriteAllLines(saveDialog.FileName, lines);
                            
                            var fileName = System.IO.Path.GetFileName(saveDialog.FileName);
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AccountsSaved", fileName, viewModel.Accounts.Count), "INFO");
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            Butterfly.Services.LocalizationManager.GetString("Msg_ErrorSavingAccounts", ex.Message),
                            Butterfly.Services.LocalizationManager.GetString("Msg_ErrorTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

        private void OpenAccountsFromFile_Click(object sender, RoutedEventArgs e)
        {
            if (!isInitialLoadComplete)
                return;

            try
            {
                var accsFolder = Path.Combine(App.RealExecutablePath, ".Butterfly", "accs");
                if (!Directory.Exists(accsFolder))
                {
                    var directoryInfo = Directory.CreateDirectory(accsFolder);
                    string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                    if (Directory.Exists(butterflyFolder))
                    {
                        var butterflyDirInfo = new DirectoryInfo(butterflyFolder);
                        if ((butterflyDirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            File.SetAttributes(butterflyFolder, butterflyDirInfo.Attributes | FileAttributes.Hidden);
                        }
                    }
                }

                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Open Accounts",
                    Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                    DefaultExt = "txt",
                    InitialDirectory = accsFolder
                };

                if (openDialog.ShowDialog() == true)
                {
                    var lines = File.ReadAllLines(openDialog.FileName);
                    var fileName = System.IO.Path.GetFileName(openDialog.FileName);
                    viewModel.Accounts.Clear();

                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                            continue;

                        if (trimmed.Contains(","))
                        {
                            var parts = trimmed.Split(',');
                            if (parts.Length == 3)
                            {
                                viewModel.Accounts.Add(new Account
                                {
                                    Character = parts[0].Trim(),
                                    Username = parts[1].Trim(),
                                    Password = parts[2].Trim(),
                                    Status = "Paused"
                                });
                            }
                        }
                                }

                                if (isPlaying)
                                {
                                    foreach (var account in viewModel.Accounts)
                                    {
                                        if (account.Status == "Online" || account.Status == "Offline")
                                        {
                                            account.Status = "Checking...";
                                        }
                                    }
                                    _ = CheckAllAccountsStatus();
                                }
                                
                                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AccountsLoaded", fileName, viewModel.Accounts.Count), "INFO");
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                Butterfly.Services.LocalizationManager.GetString("Msg_ErrorOpeningAccounts", ex.Message),
                                Butterfly.Services.LocalizationManager.GetString("Msg_ErrorTitle"),
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }

        private void ClearAccounts_Click(object sender, RoutedEventArgs e)
        {
            if (!isInitialLoadComplete)
                return;

            var count = viewModel.Accounts.Count;
            viewModel.Accounts.Clear();
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AllAccountsCleared", count), "INFO");
        }

        private void EmptyListBox_Click(object sender, MouseButtonEventArgs e)
        {
            if (!isInitialLoadComplete)
                return;

            if (viewModel.Accounts.Count == 0)
            {
                AddAccountFromContext_Click(sender, new RoutedEventArgs());
            }
        }

        private void AddAccountFromContext_Click(object sender, RoutedEventArgs e)
        {
            if (!isInitialLoadComplete)
                return;

            var newAccount = new Account
            {
                Username = "",
                Password = "",
                Character = "",
                Status = "Editing",
                IsEditing = true
            };

            viewModel.Accounts.Add(newAccount);
            AccountsGrid.SelectedItem = newAccount;
            AccountsGrid.ScrollIntoView(newAccount);

            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterAdded"), "INFO");

            Dispatcher.BeginInvoke(new Action(() =>
            {
                FocusCharacterField(newAccount);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private void FocusCharacterField(Account account)
        {
            var listBoxItem = AccountsGrid.ItemContainerGenerator.ContainerFromItem(account) as ListBoxItem;
            if (listBoxItem != null)
            {
                var characterTextBox = FindVisualChild<TextBox>(listBoxItem, "Character");
                if (characterTextBox != null)
                {
                    characterTextBox.Focus();
                    characterTextBox.SelectAll();
                }
            }
        }

        private T? FindVisualChild<T>(DependencyObject parent, string tag) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild && child is FrameworkElement element && element.Tag?.ToString() == tag)
                {
                    return typedChild;
                }

                var result = FindVisualChild<T>(child, tag);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        private void EditableTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox textBox)
            {
                if (textBox.DataContext is Account account)
                {
                    if (!string.IsNullOrWhiteSpace(account.Username) &&
                        !string.IsNullOrWhiteSpace(account.Password) &&
                        !string.IsNullOrWhiteSpace(account.Character))
                    {
                        account.IsEditing = false;
                        
                        if (isPlaying)
                        {
                            if (account.Status == "Online" || account.Status == "Offline")
                            {
                                account.Status = "Checking...";
                            }

                            _ = Task.Run(async () =>
                            {
                                bool? isOnline = await CheckCharacterOnline(account.Character);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    account.Status = isOnline == null ? "Idle" : (isOnline.Value ? "Online" : "Offline");
                                });
                            });
                        }
                        else
                        {
                            account.Status = "Paused";
                        }
                    }
                }
            }
        }

        private void BotToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                toggleButton.IsChecked = !toggleButton.IsChecked;
                
                if (toggleButton.DataContext is Account account)
                {
                    if (toggleButton.IsChecked == true)
                    {
                        _ = Task.Run(async () => await ActivateBotForAccount(account));
                    }
                    else
                    {
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_BotDisabled", account.Character), "INFO");
                    }
                }
            }
        }
        
        private async Task ActivateBotForAccount(Account account)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_BotEnabled", account.Character), "INFO");
                });
                
                bool? cachedStatus = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (rankingCache != null && 
                        (DateTime.Now - rankingCache.LastUpdate).TotalSeconds < 60 &&
                        rankingCache.CharacterStatus.ContainsKey(account.Character))
                    {
                        cachedStatus = rankingCache.CharacterStatus[account.Character];
                    }
                });
                
                if (cachedStatus == true)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!isPlaying)
                        {
                            Play();
                        }
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = true;
                });
                
                bool isOnline = await RefreshIndividualAccount(account);
                
                if (isOnline)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!isPlaying)
                        {
                            Play();
                        }
                        isConnecting = false;
                    });
                    return;
                }
                else
                {
                    int processId = 0;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        processId = account.GameProcessId;
                    });
                    
                    if (processId == 0)
                    {
                        await LaunchGameForAccount(account);
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            isConnecting = false;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorActivatingBot", account.Character, ex.Message), "ERROR");
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    int processId = account.GameProcessId;
                    if (processId == 0)
                    {
                        isConnecting = false;
                    }
                    else if (isConnecting)
                    {
                        isConnecting = false;
                    }
                });
            }
        }
        
        private async Task LaunchGameForAccount(Account account)
        {
            bool isWaiting = _reconnectionSemaphore.CurrentCount == 0;
            
            if (isWaiting)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingReconnectionQueue"), account.Character);
                });
            }
            
            await _reconnectionSemaphore.WaitAsync();
            
            bool processStarted = false;
            
            try
            {
                string gameDirectory = Path.Combine(App.RealExecutablePath, GameFolderName);
                
                if (!Directory.Exists(gameDirectory))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_GameDirectoryNotFound", gameDirectory), "ERROR");
                        account.IsBotActive = false;
                        account.IsReconnecting = false;
                        account.Status = "Offline";
                    });
                    return;
                }
                
                string mixMasterPath = Path.Combine(gameDirectory, "MixMaster.exe");
                
                if (!File.Exists(mixMasterPath))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_MixMasterNotFound", mixMasterPath), "ERROR");
                        account.IsBotActive = false;
                        account.IsReconnecting = false;
                        account.Status = "Offline";
                    });
                    return;
                }

                string arguments;
                if (selectedServer == null)
                {
                    arguments = $"3.40125 92.113.38.54 101 0 {account.Username} {account.Password} 1 AURORA_BR";
                }
                else
                {
                    switch (selectedServer.Name)
                    {
                        case "MixMaster Source":
                            arguments = $"3.40125 92.113.38.54 101 0 {account.Username} {account.Password} 1 AURORA_BR";
                            break;
                        case "MixMaster Adventure":
                            arguments = $"3.85511 54.39.96.194 22005 0 {account.Username} {account.Password} 1 AURORA_BR";
                            break;
                        case "MixMaster Origin":
                            arguments = string.Empty;
                            break;
                        default:
                            arguments = $"3.40125 92.113.38.54 101 0 {account.Username} {account.Password} 1 AURORA_BR";
                            break;
                    }
                }
                
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = mixMasterPath,
                    Arguments = arguments,
                    WorkingDirectory = gameDirectory,
                    UseShellExecute = false
                };
                
                Process? process = Process.Start(startInfo);
                
                if (process == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_FailedLaunchGame"), account.Character);
                        account.IsBotActive = false;
                        account.IsReconnecting = false;
                        account.Status = "Offline";
                    });
                    return;
                }
                
                processStarted = true;
                
                int targetProcessId = process.Id;
                await Dispatcher.InvokeAsync(() =>
                {
                    account.GameProcessId = targetProcessId;
                });
                
                IntPtr gameWindow = await WaitForWindowHandleByProcessId(targetProcessId, maxAttempts: 30, delayMs: 500, account);
                
                if (gameWindow != IntPtr.Zero)
                {
                    Win32Service.ShowWindow(gameWindow, Win32Service.SW_RESTORE);
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_LauncherBypassed", account.Character), "");
                });
                
                _ = Task.Run(async () => await WaitAndMonitorGameProcess(account));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorLaunch", ex.Message), account.Character);
                    account.IsBotActive = false;
                    account.IsReconnecting = false;
                    account.Status = "Offline";
                    
                    if (account.GameProcessId != 0)
                    {
                        try
                        {
                            var process = Process.GetProcessById(account.GameProcessId);
                            process.Kill();
                        }
                        catch
                        {
                            // process no longer exists or cannot be closed
                        }
                        account.GameProcessId = 0;
                    }
                });
            }
            finally
            {
                if (!processStarted)
                {
                    _reconnectionSemaphore.Release();
                    
                    await Dispatcher.InvokeAsync(() =>
                    {
                        account.IsReconnecting = false;
                    });
                }
            }
        }

        private async Task<IntPtr> WaitForWindowHandleByProcessId(int processId, int maxAttempts = 10, int delayMs = 500, Account? account = null)
        {
            Process? process = null;
            
            try
            {
                try
                {
                    process = Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    return IntPtr.Zero;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return IntPtr.Zero;
                }
                catch (InvalidOperationException)
                {
                    return IntPtr.Zero;
                }

                try
                {
                    if (!process.HasExited)
                    {
                        process.WaitForInputIdle(5000);
                    }
                }
                catch (InvalidOperationException)
                {
                    // process has no GUI or was already disposed
                    // continue trying anyway
                }

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        process.Refresh();

                        if (process.HasExited)
                        {
                            return IntPtr.Zero;
                        }

                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            Win32Service.GetWindowThreadProcessId(process.MainWindowHandle, out uint windowProcessId);
                            if (windowProcessId == processId && Win32Service.IsWindowVisible(process.MainWindowHandle))
                            {
                                return process.MainWindowHandle;
                            }
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return IntPtr.Zero;
                    }
                    catch (InvalidOperationException)
                    {
                        return IntPtr.Zero;
                    }
                    catch (ArgumentException)
                    {
                        return IntPtr.Zero;
                    }

                    if (attempt < maxAttempts - 1)
                    {
                        await Task.Delay(delayMs);
                    }
                }
            }
            catch
            {
                return IntPtr.Zero;
            }

            if (account != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (account.GameProcessId == processId)
                    {
                        try
                        {
                            var timeoutProcess = Process.GetProcessById(processId);
                            if (!timeoutProcess.HasExited)
                            {
                                timeoutProcess.Kill();
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // process no longer exists
                        }
                        catch
                        {
                            // any other error
                        }
                        account.GameProcessId = 0;
                    }
                });
            }
            else
            {
                try
                {
                    var timeoutProcess = Process.GetProcessById(processId);
                    if (!timeoutProcess.HasExited)
                    {
                        timeoutProcess.Kill();
                    }
                }
                catch
                {
                    // process no longer exists or cannot be closed
                }
            }

            return IntPtr.Zero;
        }

        private IntPtr GetWindowHandleDynamically(int processId)
        {
            try
            {
                Process? process = null;
                try
                {
                    process = Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    return IntPtr.Zero;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return IntPtr.Zero;
                }
                catch (InvalidOperationException)
                {
                    return IntPtr.Zero;
                }

                process.Refresh();

                if (process.HasExited)
                {
                    return IntPtr.Zero;
                }

                return process.MainWindowHandle;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                return IntPtr.Zero;
            }
            catch (ArgumentException)
            {
                return IntPtr.Zero;
            }
        }


        private async Task SendClickToWindow(IntPtr hwnd, int relativeX, int relativeY)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorInvalidHandle"), "ERROR");
                });
                return;
            }
            
            if (!Win32Service.IsWindowVisible(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"ERROR: Game window is not visible!", "ERROR");
                });
                return;
            }
            
            bool focusObtained = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr activeHandle = Win32Service.GetForegroundWindow();
                if (activeHandle == hwnd)
                {
                    focusObtained = true;
                    break;
                }
                
                if (attempt > 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"Fighting for focus... attempt {attempt + 1}", "WARNING");
                    });
                }
                
                await ForceWindowFocus(hwnd);
                await Task.Delay(200);
                
                activeHandle = Win32Service.GetForegroundWindow();
                if (activeHandle == hwnd)
                {
                    focusObtained = true;
                    break;
                }
            }
            
            if (!focusObtained)
            {
                IntPtr finalActiveHandle = Win32Service.GetForegroundWindow();
                if (finalActiveHandle != hwnd)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"ERROR: Failed to obtain window focus after 3 attempts. Click aborted.", "ERROR");
                    });
                    return;
                }
            }
            
            Win32Service.POINT targetPoint = new Win32Service.POINT { X = relativeX, Y = relativeY };
            
            if (!Win32Service.ClientToScreen(hwnd, ref targetPoint))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"ERROR: Failed to convert client coordinates to screen coordinates!", "ERROR");
                });
                return;
            }
            
            Win32Service.SetCursorPos(targetPoint.X, targetPoint.Y);
            await Task.Delay(50);
            
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private async Task ForceWindowFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
                return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                Win32Service.ShowWindow(hwnd, Win32Service.SW_RESTORE);
                
                Win32Service.SetForegroundWindow(hwnd);
                
                await Task.Delay(500);

                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == hwnd)
                {
                    return;
                }
            }
        }

        private async Task SendDoubleClickToWindow(int processId, int relativeX, int relativeY)
        {
            IntPtr hwnd = GetWindowHandleDynamically(processId);
            
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorInvalidHandleDoubleClick", processId), "ERROR");
                });
                return;
            }

            try
            {
                var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorProcessExited", processId), "ERROR");
                    });
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    process.Kill();
                }
                catch { }
                throw new Exception("Process died unexpectedly.");
            }
            catch (InvalidOperationException)
            {
                throw new Exception("Process died unexpectedly.");
            }
            catch (ArgumentException)
            {
                throw new Exception("Process died unexpectedly.");
            }

            await SendDoubleClickToWindow(hwnd, relativeX, relativeY);
        }

        private async Task SendDoubleClickToWindow(IntPtr hwnd, int relativeX, int relativeY)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorInvalidHandleDoubleClick2"), "ERROR");
                });
                return;
            }
            
            if (!Win32Service.IsWindowVisible(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorWindowNotVisible2"), "ERROR");
                });
                return;
            }
            
            GetWindowThreadProcessId(hwnd, out uint targetProcessId);
            bool focusObtained = false;
            
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr activeHandle = Win32Service.GetForegroundWindow();
                GetWindowThreadProcessId(activeHandle, out uint activeProcessId);
                
                if (activeProcessId == targetProcessId && activeHandle == hwnd)
                {
                    focusObtained = true;
                    break;
                }
                
                if (attempt > 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"Fighting for focus... attempt {attempt + 1}", "WARNING");
                    });
                }
                
                await ForceWindowFocus(hwnd);
                await Task.Delay(200);
                
                activeHandle = Win32Service.GetForegroundWindow();
                GetWindowThreadProcessId(activeHandle, out uint newActiveProcessId);
                if (newActiveProcessId == targetProcessId && activeHandle == hwnd)
                {
                    focusObtained = true;
                    break;
                }
            }
            
            if (!focusObtained)
            {
                IntPtr finalActiveHandle = Win32Service.GetForegroundWindow();
                GetWindowThreadProcessId(finalActiveHandle, out uint finalActiveProcessId);
                if (finalActiveProcessId != targetProcessId || finalActiveHandle != hwnd)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"ERROR: Failed to obtain window focus after 3 attempts. Click aborted.", "ERROR");
                    });
                    return;
                }
            }
            
            Win32Service.POINT targetPoint = new Win32Service.POINT { X = relativeX, Y = relativeY };
            
            if (!Win32Service.ClientToScreen(hwnd, ref targetPoint))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorConvertCoordinates"), "ERROR");
                });
                return;
            }
            
            Win32Service.SetCursorPos(targetPoint.X, targetPoint.Y);
            await Task.Delay(100);
            
            uint lParam = (uint)((relativeY << 16) | (relativeX & 0xFFFF));
            
            Win32Service.PostMessage(hwnd, Win32Service.WM_LBUTTONDBLCLK, (IntPtr)0x0001, (IntPtr)lParam);
            await Task.Delay(50);
            
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(75);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            
            await Task.Delay(100);
        }

        private async Task SendTextToWindow(IntPtr hwnd, string text)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
                return;
            
            foreach (char c in text)
            {
                Win32Service.PostMessage(hwnd, Win32Service.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                await Task.Delay(30);
            }
        }

        private async Task SendKey(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
                return;
            
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
        }

        private async Task SendKeyToWindow(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
                return;
            
            SetForegroundWindow(hwnd);
            await Task.Delay(100);
            
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
        }

        private async Task SendKeyToWindow(int processId, int vkCode)
        {
            IntPtr hwnd = GetWindowHandleDynamically(processId);
            
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                return;
            }

            try
            {
                var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    process.Kill();
                }
                catch { }
                throw new Exception("Process died unexpectedly.");
            }
            catch (InvalidOperationException)
            {
                throw new Exception("Process died unexpectedly.");
            }
            catch (ArgumentException)
            {
                throw new Exception("Process died unexpectedly.");
            }
            
            SetForegroundWindow(hwnd);
            await Task.Delay(100);
            
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
        }

        private async Task ClickButton(IntPtr buttonHandle)
        {
            if (buttonHandle == IntPtr.Zero || !IsWindow(buttonHandle))
                return;
            
            Win32Service.PostMessage(buttonHandle, Win32Service.WM_LBUTTONDOWN, (IntPtr)0x0001, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(buttonHandle, Win32Service.WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        }

        private async Task WaitAndMonitorGameProcess(Account account)
        {
            try
            {
                int targetProcessId = 0;
                await Dispatcher.InvokeAsync(() =>
                {
                    targetProcessId = account.GameProcessId;
                });
                
                if (targetProcessId == 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorProcessNotFound"), account.Character);
                        account.IsBotActive = false;
                    });
                    return;
                }
                
                Process? targetProcess = null;
                try
                {
                    targetProcess = Process.GetProcessById(targetProcessId);
                }
                catch (ArgumentException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorProcessNotFound2", targetProcessId), account.Character);
                        account.IsBotActive = false;
                    });
                    return;
                }
                
                await SelectServerInGame(targetProcessId, account);
                
                await MonitorGameProcess(targetProcessId, account);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorWaitingGameProcess", ex.Message), "ERROR");
                });
            }
        }

        private async Task SelectServerInGame(int processId, Account account)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = true;
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingServerScreen", account.Character), "");
                });
                
                IntPtr gameWindow = await WaitForWindowHandleByProcessId(processId, maxAttempts: 30, delayMs: 500, account);
                
                if (gameWindow == IntPtr.Zero)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_GameWindowNotFound", processId), account.Character);
                        
                        try
                        {
                            var process = Process.GetProcessById(processId);
                            process.Kill();
                        }
                        catch
                        {
                            // process no longer exists or cannot be closed
                        }
                    });
                    return;
                }

                Win32Service.ShowWindow(gameWindow, Win32Service.SW_RESTORE);
                
                var (screenDetected, newWindow) = await WaitForServerSelectionScreen(gameWindow, processId, account);
                
                if (newWindow != IntPtr.Zero)
                {
                    gameWindow = newWindow;
                }
                
                if (!screenDetected)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ServerScreenTimeout", account.Character), "");
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ServerScreenDetected", account.Character), "");
                });
                
                bool focusObtained = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr activeHandle = Win32Service.GetForegroundWindow();
                    GetWindowThreadProcessId(activeHandle, out uint activeProcessId);
                    
                    if (activeProcessId == processId && activeHandle == gameWindow)
                    {
                        focusObtained = true;
                        break;
                    }
                    
                    if (attempt > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WarningFightingFocus", attempt + 1), account.Character);
                        });
                    }
                    
                    Win32Service.ShowWindow(gameWindow, Win32Service.SW_RESTORE);
                    Win32Service.SetForegroundWindow(gameWindow);
                    await Task.Delay(200);
                }
                
                if (!focusObtained)
                {
                    IntPtr finalActiveHandle = Win32Service.GetForegroundWindow();
                    GetWindowThreadProcessId(finalActiveHandle, out uint finalActiveProcessId);
                    if (finalActiveProcessId != processId || finalActiveHandle != gameWindow)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorFailedFocus"), account.Character);
                            account.IsReconnecting = false;
                            account.Status = "Offline";
                            isConnecting = false;
                            
                            if (account.GameProcessId != 0)
                            {
                                try
                                {
                                    var process = Process.GetProcessById(account.GameProcessId);
                                    process.Kill();
                                }
                                catch
                                {
                                    // process no longer exists or cannot be closed
                                }
                                account.GameProcessId = 0;
                            }
                        });
                        return;
                    }
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConnectingToServer", account.Character), "");
                });
                
                int serverX = 815;
                int serverY = 310;
                
                await SendDoubleClickToWindow(processId, serverX, serverY);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingCharacterScreen", account.Character), "");
                });
                
                bool characterScreenDetected = await WaitForCharacterSelectionScreen(gameWindow, processId, account);
                
                if (!characterScreenDetected)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterScreenTimeout", account.Character), "");
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterScreenDetected", account.Character), "");
                });
                
                int enterX = 600;
                int enterY = 654;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConnectingToCharacter", account.Character), "");
                });
                
                await SendDoubleClickToWindow(processId, enterX, enterY);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingGameMap", account.Character), "");
                });
                
                bool mapLoaded = await WaitForGameMapLoad(gameWindow, processId, account);
                
                if (!mapLoaded)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0;
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_GameMapTimeout", account.Character), "");
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_Connected", account.Character), "");
                });
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ActivatingAutoPlay", account.Character), "");
                });
                
                await SendKeyToWindow(processId, VK_F12);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AutoPlayActivated", account.Character), "");
                });
                
                await Task.Delay(2000);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                });
                
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!isPlaying)
                    {
                        Play();
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    account.IsReconnecting = false;
                    account.Status = "Offline";
                    
                    if (account.GameProcessId != 0)
                    {
                        try
                        {
                            var process = Process.GetProcessById(account.GameProcessId);
                            process.Kill();
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // process no longer exists
                        }
                        catch
                        {
                            // any other error
                        }
                        account.GameProcessId = 0;
                    }
                    
                    LogToConsole($"Error selecting server: {ex.Message}", "ERROR");
                });
            }
            finally
            {
                _reconnectionSemaphore.Release();
            }
        }

        private async Task<(bool success, IntPtr newWindow)> WaitForServerSelectionScreen(IntPtr gameWindow, int processId, Account account)
        {
            try
            {
                int attempts = 0;
                int maxAttempts = 20;
                IntPtr currentWindow = gameWindow;
                
                while (attempts < maxAttempts)
                {
                    await Task.Delay(500);
                    attempts++;
                    
                    try
                    {
                        var process = Process.GetProcessById(processId);
                        process.Refresh();
                        if (process.HasExited)
                        {
                            return (false, IntPtr.Zero);
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return (false, IntPtr.Zero);
                    }
                    catch (InvalidOperationException)
                    {
                        return (false, IntPtr.Zero);
                    }
                    catch (ArgumentException)
                    {
                        return (false, IntPtr.Zero);
                    }

                    if (!IsWindow(currentWindow) || !IsWindowVisible(currentWindow))
                    {
                        IntPtr newWindow = GetWindowHandleDynamically(processId);
                        if (newWindow != IntPtr.Zero && IsWindow(newWindow) && IsWindowVisible(newWindow))
                        {
                            currentWindow = newWindow;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    
                    IntPtr foregroundWindow = GetForegroundWindow();
                    if (foregroundWindow != currentWindow)
                    {
                        SetForegroundWindow(currentWindow);
                        await Task.Delay(100);
                    }
                    
                    IntPtr hdc = GetDC(currentWindow);
                    if (hdc != IntPtr.Zero)
                    {
                        try
                        {
                            uint pixelSource = Win32Service.GetPixel(hdc, 815, 350);
                            byte r1 = (byte)(pixelSource & 0xFF);
                            byte g1 = (byte)((pixelSource >> 8) & 0xFF);
                            byte b1 = (byte)((pixelSource >> 16) & 0xFF);                            
                            bool isSource = (r1 > 130 && g1 > 80) && (b1 < 170);

                            uint pixelAdventure = Win32Service.GetPixel(hdc, 815, 350);
                            byte r2 = (byte)(pixelAdventure & 0xFF);
                            byte g2 = (byte)((pixelAdventure >> 8) & 0xFF);
                            byte b2 = (byte)((pixelAdventure >> 16) & 0xFF);
                            bool isAdventure = (r2 > 190 && r2 < 220) && (g2 > 180 && g2 < 210) && (b2 > 160 && b2 < 190);

                            uint pixelLoaded = Win32Service.GetPixel(hdc, 733, 309);
                            byte r3 = (byte)(pixelLoaded & 0xFF);
                            byte g3 = (byte)((pixelLoaded >> 8) & 0xFF);
                            byte b3 = (byte)((pixelLoaded >> 16) & 0xFF);
                            bool isLoaded = !((r3 > 195 && r3 < 220) && (g3 > 185 && g3 < 210) && (b3 > 160 && b3 < 185));

                            if ((isSource || isAdventure) && isLoaded)
                            {
                                ReleaseDC(currentWindow, hdc);
                                await Task.Delay(1000);
                                return (true, currentWindow);
                            }
                        }
                        finally
                        {
                            ReleaseDC(currentWindow, hdc);
                        }
                    }
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    LogToConsole($"Timeout: Server screen not detected.", account.Character);
                    
                    if (account.GameProcessId == processId)
                    {
                        try
                        {
                            var process = Process.GetProcessById(processId);
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // process no longer exists
                        }
                        catch
                        {
                            // any other error
                        }
                        account.GameProcessId = 0;
                    }
                });
                
                return (false, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"Error detecting screen: {ex.Message}", "ERROR");
                });
                return (false, IntPtr.Zero);
            }
        }

        private async Task<bool> WaitForCharacterSelectionScreen(IntPtr gameWindow, int processId, Account account)
        {
            try
            {
                int attempts = 0;
                int maxAttempts = 20;
                IntPtr currentWindow = gameWindow;
                
                while (attempts < maxAttempts)
                {
                    await Task.Delay(500);
                    attempts++;
                    
                    try
                    {
                        var process = Process.GetProcessById(processId);
                        process.Refresh();
                        if (process.HasExited)
                        {
                            return false;
                        }
                        
                        IntPtr newWindow = GetWindowHandleDynamically(processId);
                        if (newWindow != IntPtr.Zero && IsWindow(newWindow) && IsWindowVisible(newWindow))
                        {
                            currentWindow = newWindow;
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                    
                    IntPtr hdc = GetDC(currentWindow);
                    if (hdc != IntPtr.Zero)
                    {
                        try
                        {
                            uint pixelSource = Win32Service.GetPixel(hdc, 590, 654);
                            byte r1 = (byte)(pixelSource & 0xFF);
                            byte g1 = (byte)((pixelSource >> 8) & 0xFF);
                            byte b1 = (byte)((pixelSource >> 16) & 0xFF);                            
                            bool hasSourceButton = (b1 > 100 && b1 > r1 && b1 > g1) ||
                                                 (b1 > 80 && g1 > 80 && r1 < 100);

                            uint pixelAdventure = Win32Service.GetPixel(hdc, 590, 654);
                            byte r2 = (byte)(pixelAdventure & 0xFF);
                            byte g2 = (byte)((pixelAdventure >> 8) & 0xFF);
                            byte b2 = (byte)((pixelAdventure >> 16) & 0xFF);
                            bool hasAdventureButton = (r2 > 225 && r2 < 250) && (g2 > 215 && g2 < 240) && (b2 > 210 && b2 < 235) &&
                                                      (Math.Abs(r2 - g2) < 20) && (Math.Abs(g2 - b2) < 15);

                            uint pixelLoaded = Win32Service.GetPixel(hdc, 508, 504);
                            byte r3 = (byte)(pixelLoaded & 0xFF);
                            byte g3 = (byte)((pixelLoaded >> 8) & 0xFF);
                            byte b3 = (byte)((pixelLoaded >> 16) & 0xFF);
                            bool isLoaded = !((r3 > 130 && r3 < 150) && (g3 > 170 && g3 < 185) && (b3 > 125 && b3 < 140) &&
                                              (g3 > r3 && g3 > b3));
                            
                            if ((hasSourceButton || hasAdventureButton) && isLoaded)
                            {
                                ReleaseDC(currentWindow, hdc);
                                return true;
                            }
                        }
                        finally
                        {
                            ReleaseDC(currentWindow, hdc);
                        }
                    }
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    if (account.GameProcessId == processId)
                    {
                        try
                        {
                            var process = Process.GetProcessById(processId);
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // process no longer exists
                        }
                        catch
                        {
                            // any other error
                        }
                        account.GameProcessId = 0;
                    }
                });
                
                return false;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"Error detecting character screen: {ex.Message}", "ERROR");
                });
                return false;
            }
        }

        private async Task<bool> WaitForGameMapLoad(IntPtr gameWindow, int processId, Account account)
        {
            try
            {
                int attempts = 0;
                int maxAttempts = 20;
                IntPtr currentWindow = gameWindow;
                
                while (attempts < maxAttempts)
                {
                    await Task.Delay(500);
                    attempts++;
                    
                    try
                    {
                        var process = Process.GetProcessById(processId);
                        process.Refresh();
                        if (process.HasExited)
                        {
                            return false;
                        }
                        
                        IntPtr newWindow = GetWindowHandleDynamically(processId);
                        if (newWindow != IntPtr.Zero && IsWindow(newWindow) && IsWindowVisible(newWindow))
                        {
                            currentWindow = newWindow;
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                    
                    IntPtr hdc = GetDC(currentWindow);
                    if (hdc != IntPtr.Zero)
                    {
                        try
                        {
                            uint minimapPixel = Win32Service.GetPixel(hdc, 80, 80);
                            
                            uint iconBarPixel = Win32Service.GetPixel(hdc, 512, 738);
                            
                            byte rMini = (byte)(minimapPixel & 0xFF);
                            byte gMini = (byte)((minimapPixel >> 8) & 0xFF);
                            byte bMini = (byte)((minimapPixel >> 16) & 0xFF);
                            
                            byte rIcon = (byte)(iconBarPixel & 0xFF);
                            byte gIcon = (byte)((iconBarPixel >> 8) & 0xFF);
                            byte bIcon = (byte)((iconBarPixel >> 16) & 0xFF);
                            
                            bool hasMinimapOrUI = (bMini > 50 || gMini > 50) ||
                                                  (rIcon > 20 && gIcon > 20 && bIcon > 20) ||
                                                  (rIcon + gIcon + bIcon > 100);
                            
                            if (hasMinimapOrUI && attempts > 3)
                            {
                                ReleaseDC(currentWindow, hdc);
                                return true;
                            }
                        }
                        finally
                        {
                            ReleaseDC(currentWindow, hdc);
                        }
                    }
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    if (account.GameProcessId == processId)
                    {
                        try
                        {
                            var process = Process.GetProcessById(processId);
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // process no longer exists
                        }
                        catch
                        {
                            // any other error
                        }
                        account.GameProcessId = 0;
                    }
                });
                
                return false;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorDetectingGameMap", ex.Message), "ERROR");
                });
                return false;
            }
        }

        private async Task MonitorGameProcess(int processId, Account account)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(2000);
                    
                    try
                    {
                        var process = Process.GetProcessById(processId);
                    }
                    catch (ArgumentException)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            account.GameProcessId = 0;
                        });
                        
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"Error monitoring character '{account.Character}': {ex.Message}", "ERROR");
                });
            }
        }
        
        private async Task AutoReconnectLoop(Account account)
        {
            try
            {
                bool alreadyReconnecting = false;
                string characterName = string.Empty;
                await Dispatcher.InvokeAsync(() =>
                {
                    alreadyReconnecting = account.IsReconnecting;
                    characterName = account.Character;
                    if (!alreadyReconnecting)
                    {
                        account.IsReconnecting = true;
                    }
                });
                
                if (alreadyReconnecting)
                {
                    return;
                }
                
                bool isFirstIteration = true;
                
                while (true)
                {
                    bool shouldContinue = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        shouldContinue = account.IsBotActive && account.GameProcessId == 0;
                    });
                    
                    if (!shouldContinue)
                    {
                        break;
                    }
                    
                    if (!isFirstIteration)
                    {
                        await Task.Delay(1000);
                        
                        bool isOnline = await RefreshIndividualAccount(account);
                        
                        if (isOnline)
                        {
                            break;
                        }
                        
                        await Dispatcher.InvokeAsync(() =>
                        {
                            shouldContinue = account.IsBotActive && account.GameProcessId == 0;
                        });
                        
                        if (!shouldContinue)
                        {
                            break;
                        }
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterDisconnectedStarting", characterName), "");
                        });
                        isFirstIteration = false;
                    }
                    
                    await LaunchGameForAccount(account);
                    
                    bool connected = await WaitForGameConnection(account, timeoutSeconds: 30);
                    
                    if (connected)
                    {
                        await Task.Delay(1000);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"Error in bot loop for '{account.Character}': {ex.Message}", "ERROR");
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    account.IsReconnecting = false;
                });
            }
        }
        
        private async Task<bool> WaitForGameConnection(Account account, int timeoutSeconds)
        {
            int attempts = 0;
            int maxAttempts = timeoutSeconds * 2;
            
            while (attempts < maxAttempts)
            {
                int currentProcessId = 0;
                bool shouldContinue = false;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    currentProcessId = account.GameProcessId;
                    shouldContinue = account.IsBotActive;
                });
                
                if (!shouldContinue)
                {
                    return false;
                }
                
                if (currentProcessId > 0)
                {
                    return true;
                }
                
                await Task.Delay(500);
                attempts++;
            }
            
            return false;
        }

        private void AccountsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && draggedItem == null)
            {
                var listBox = sender as ListBox;
                if (listBox == null) return;

                var element = e.OriginalSource as DependencyObject;
                if (element != null && (element is TextBox || FindParent<TextBox>(element) != null))
                {
                    return;
                }

                var mousePosition = e.GetPosition(listBox);
                var item = GetItemAtPosition(listBox, mousePosition);

                if (item != null && item is Account account)
                {
                    if (account.IsEditing)
                        return;

                    draggedItem = account;
                    DragDrop.DoDragDrop(listBox, account, DragDropEffects.Move);
                    draggedItem = null;
                }
            }
        }

        private void AccountsGrid_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.SizeNS);
            e.Handled = true;
        }

        private T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);

            if (parentObject == null)
                return null;

            if (parentObject is T parent)
                return parent;

            return FindParent<T>(parentObject);
        }

        private void AccountsGrid_DragOver(object sender, DragEventArgs e)
        {
            if (draggedItem == null || e.Data.GetData(typeof(Account)) is not Account droppedAccount)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            e.Effects = DragDropEffects.Move;

            var listBox = sender as ListBox;
            if (listBox == null) return;

            var mousePosition = e.GetPosition(listBox);
            var targetItem = GetItemAtPosition(listBox, mousePosition);

            if (targetItem != null && targetItem is Account targetAccount && droppedAccount != targetAccount)
            {
                int oldIndex = viewModel.Accounts.IndexOf(droppedAccount);
                int newIndex = viewModel.Accounts.IndexOf(targetAccount);

                if (oldIndex != -1 && newIndex != -1 && oldIndex != newIndex)
                {
                    viewModel.Accounts.Move(oldIndex, newIndex);
                }
            }
        }

        private void AccountsGrid_Drop(object sender, DragEventArgs e)
        {
            // drag and drop completed
        }

        private object? GetItemAtPosition(ListBox listBox, Point position)
        {
            var element = listBox.InputHitTest(position) as UIElement;
            while (element != null)
            {
                if (element is ListBoxItem listBoxItem)
                {
                    return listBoxItem.DataContext;
                }
                element = System.Windows.Media.VisualTreeHelper.GetParent(element) as UIElement;
            }
            return null;
        }

        private async void ServersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignorar mudanças durante request em progresso
            if (_isServerRequestInProgress)
            {
                return;
            }
            
            if (ServersGrid.SelectedItem is Server server)
            {
                bool isServerChange = selectedServer != null && selectedServer != server;
                
                if (selectedServer != null)
                {
                    selectedServer.IsSelected = false;
                }

                selectedServer = server;
                selectedServer.IsSelected = true;
                
                viewModel.IsServerSelected = true;

                rankingCache = null;

                if (isPlaying && isServerChange)
                {
                    try
                    {
                        autoRefreshCancellationTokenSource?.Cancel();
                    }
                    catch
                    {
                        // ignore errors when canceling - StartAutoRefreshTimer will do cleanup
                    }
                    
                    await Task.Delay(100);
                    
                    StartAutoRefreshTimer();
                }
                else if (!isPlaying)
                {
                    Play();
                }
            }
        }

        public void UpdateWindowTitle()
        {
            string fullTitle = App.GetFormattedTitle(App.LicenseType);

            this.Title = fullTitle;

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                Butterfly.Native.Win32Service.SetWindowText(helper.Handle, fullTitle);
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateWindowTitle();
            Butterfly.Helpers.FocusHelper.ForceForeground(this);
            
            InitializeHeavyOperations();
        }
        
        private void InitializeHeavyOperations()
        {
            loggerHelper.InitializeLogging();
            if (txtLastLog != null)
            {
                txtLastLog.Text = Butterfly.Services.LocalizationManager.GetString("Log_WaitingForEvents");
            }
            
            if (consoleWindow != null)
            {
                consoleWindow.AppendLog(Butterfly.Services.LocalizationManager.GetString("Console_Welcome_1"));
                consoleWindow.AppendLog(Butterfly.Services.LocalizationManager.GetString("Console_Welcome_2"));
                consoleWindow.AppendLog(Butterfly.Services.LocalizationManager.GetString("Console_Welcome_3"));
            }

            InitializeServers();
            
            LoadAccountsAutomatically();

            foreach (var account in viewModel.Accounts)
            {
                account.Status = "Paused";
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool hasUpdate = false;
                    
                    if (hasUpdate)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            LogToConsole("New version detected. Mandatory update starting...", "INFO");
                            ShowUpdateOverlay();
                            
                            try
                            {
                                if (UpdateProgressBar != null)
                                {
                                    UpdateProgressBar.IsIndeterminate = false;
                                    UpdateProgressBar.Minimum = 0;
                                    UpdateProgressBar.Maximum = 100;
                                    UpdateProgressBar.Value = 0;
                                }
                                
                                var progress = new Progress<double>(percent =>
                                {
                                    if (UpdateProgressBar != null)
                                    {
                                        UpdateProgressBar.Value = percent;
                                    }
                                });
                                
                                bool success = await updateService.DownloadAndInstallUpdateAsync(progress);
                                
                                if (!success)
                                {
                                    LogToConsole("Mandatory update failed. Please check your internet connection and try again.", "ERROR");
                                    LogToConsole("You can close the application and try again later.", "INFO");
                                    
                                }
                            }
                            catch (Exception ex)
                            {
                                LogToConsole($"Mandatory update failed: {ex.Message}", "ERROR");
                                LogToConsole("Please check your internet connection and antivirus settings.", "ERROR");
                                LogToConsole("You can close the application and try again later.", "INFO");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogToConsole($"Update check failed: {ex.Message}", "ERROR");
                }
            });
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            App.OnLicenseTypeChanged -= MainWindow_OnLicenseTypeChanged;
        }

        private void MainWindow_OnLicenseTypeChanged(string newTier)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                UpdateWindowTitle();
            }));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}
