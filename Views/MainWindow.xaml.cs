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
            // Make the process DPI-aware so mouse coordinates work correctly
            Win32Service.SetProcessDPIAware();
            
            InitializeComponent();
            
            // Set DataContext to allow bindings in the Window itself
            this.DataContext = this;
            
            // Initialize floating button tooltip
            UpdateFabTooltipState();

            // Check if running as administrator
            if (!SecurityHelper.IsRunningAsAdministrator())
            {
                MessageBox.Show(
                    Butterfly.Services.LocalizationManager.GetString("Msg_AdminRequired"),
                    Butterfly.Services.LocalizationManager.GetString("Msg_AdminRequiredTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
            }

            // Initialize logger helper (now fails silently if there are problems)
            // Initialize logger helper (actual initialization moved to Loaded)
            loggerHelper = new LoggerHelper();

            // Definir caminho do arquivo de salvamento autom�tico
            // Define path for auto-save file (inside .Butterfly folder)
            string dataFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
            if (!Directory.Exists(dataFolder))
            {
                var directoryInfo = Directory.CreateDirectory(dataFolder);
                // Make the folder hidden
                if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                {
                    File.SetAttributes(dataFolder, directoryInfo.Attributes | FileAttributes.Hidden);
                }
            }
            autoSaveFilePath = Path.Combine(dataFolder, "accounts.dat");

            // Initialize services (lightweight setup - heavy initialization moved to Loaded)
            accountDataService = new AccountDataService(autoSaveFilePath);
            accountStatsService = new AccountStatsService();
            gameApiService = new GameApiService();
            updateService = new UpdateService();

            // Initialize ViewModel
            viewModel = new MainViewModel();
            this.DataContext = viewModel;
            
            // Ensure language is set from ViewModel (may have saved preference)
            if (viewModel.SelectedLanguage != null)
            {
                Butterfly.Services.LocalizationManager.Instance.SwitchLanguage(viewModel.SelectedLanguage.Code);
            }

            // Create console window (don't show automatically) - needed for LogToConsole
            consoleWindow = new ConsoleWindow(this); // Pass MainWindow reference
            consoleWindow.Closing += ConsoleWindow_Closing;

            // Connect service logs to internal UI console with thread safety
            // Use Application.Current.Dispatcher to ensure thread safety even from async threads
            // AppendLog and LogToConsole already use Dispatcher internally, but ensure here too for safety
            var uiDispatcher = Application.Current.Dispatcher;
            gameApiService.OnLogRequested = (msg, type) => uiDispatcher.Invoke(() => LogToConsole(msg, type));
            updateService.OnLogRequested = (msg, type) => uiDispatcher.Invoke(() => LogToConsole(msg, type));

            // Apply dark title bar (lightweight - also called in OnSourceInitialized)
            ApplyDarkTitleBar();

            // Enable interactions immediately (no initial loading)
            isInitialLoadComplete = true;
            SetContextMenusEnabled(true);

            // Auto-save on close
            this.Closing += MainWindow_Closing;
            
            // Add Loaded event to update title when window loads
            this.Loaded += MainWindow_Loaded;
            
            // Add Unloaded event to unsubscribe and prevent memory leaks
            this.Unloaded += MainWindow_Unloaded;
            
            // Subscribe to LicenseType change event
            App.OnLicenseTypeChanged += MainWindow_OnLicenseTypeChanged;
            
            // Ensure execution at end of constructor
            UpdateWindowTitle();
        }

        // Public methods to control play/pause
        public async void Play()
        {
            // Block interactions if update overlay is visible
            if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotStartMonitoring"), "INFO");
                return;
            }
            
            // Auto-select first server if none is selected
            if (selectedServer == null && viewModel.Servers.Any())
            {
                var firstServer = viewModel.Servers.First();
                // Setting SelectedItem will trigger ServersGrid_SelectionChanged event
                // which will handle all the selection logic (IsSelected, IsServerSelected, etc.)
                ServersGrid.SelectedItem = firstServer;
            }
            
            if (isPlaying) return;
            
            isPlaying = true;
            
            // Update visual state of Play/Pause button in ConsoleWindow and MainWindow
            consoleWindow?.UpdatePlayPauseState(true);
            UpdatePlayPauseIcon();
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_StartingMonitoring"), "INFO");
            
            // Allow global requests after monitoring officially starts
            isConnecting = false;
            
            // First change status of SELECTED server to "Checking..." (yellow)
            if (selectedServer != null)
            {
                selectedServer.Status = "Checking...";
            }
            
            // Change only accounts with Online or Offline status to "Checking..."
            // Idle characters (gray) should not change to yellow during monitoring
            foreach (var account in viewModel.Accounts)
            {
                if (account.Status == "Online" || account.Status == "Offline")
                {
                    account.Status = "Checking...";
                }
            }
            
            // Wait a bit to visualize the yellow
            await Task.Delay(100);
            
            // Start automatic refresh - the loop will do the first check
            // DON'T do check here to avoid double request with the loop
            StartAutoRefreshTimer();
        }

        public void Pause()
        {
            if (!isPlaying) return;
            
            isPlaying = false;
            
            // Update visual state of Play/Pause button in ConsoleWindow and MainWindow
            consoleWindow?.UpdatePlayPauseState(false);
            UpdatePlayPauseIcon();
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_MonitoringPaused"), "INFO");
            
            // Stop automatic refresh loop
            autoRefreshCancellationTokenSource?.Cancel();
            autoRefreshCancellationTokenSource?.Dispose();
            autoRefreshCancellationTokenSource = null;
            
            // Clear server selection to visually indicate no server is active
            if (selectedServer != null)
            {
                selectedServer.IsSelected = false;
                selectedServer = null;
            }
            ServersGrid.SelectedItem = null;
            viewModel.IsServerSelected = false;
            
            // Reset visual status of ALL characters to "Idle" (gray)
            // This visually indicates that no character is being monitored
            foreach (var account in viewModel.Accounts)
            {
                account.Status = "Idle";
                account.Level = string.Empty;
                account.Experience = string.Empty;
            }
            
            // IMPORTANT: Servers maintain their status (Online/Offline) - they are not affected
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

                // Trigger reconnection in background
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
            // Block if update overlay is visible
            if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotRefresh"), "INFO");
                return;
            }
            
            // Check if there is a selected server
            if (selectedServer == null)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotRefreshNoServer"), "INFO");
                return;
            }

            // Check if connecting - if so, don't refresh (respect login exclusivity)
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
            
            // Change status of SELECTED server to "Checking..." (yellow)
            selectedServer.Status = "Checking...";
            
            // Change only accounts with Online or Offline status to "Checking..."
            // Idle characters (gray) should not change to yellow during monitoring
            foreach (var account in viewModel.Accounts)
            {
                if (account.Status == "Online" || account.Status == "Offline")
                {
                    account.Status = "Checking...";
                }
            }
            
            // Wait a bit to visualize the yellow
            await Task.Delay(100);
            
            // Do a single check (will change to Online/Offline)
            // Ignores delay and forces immediate request
            await CheckAllServersAndAccountsStatus();
            
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_RefreshComplete"), "INFO");
        }

        // Methods for console controls in MainWindow
        private void ConsoleWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // When ConsoleWindow is closed, restore visibility of controls bar
            ConsoleControlsBar.Visibility = Visibility.Visible;
        }

        private void TerminalButton_Click(object sender, RoutedEventArgs e)
        {
            if (consoleWindow == null) return;

            if (consoleWindow.IsVisible)
            {
                // If terminal is open, close it
                consoleWindow.Hide();
                ConsoleControlsBar.Visibility = Visibility.Visible;
            }
            else
            {
                // If terminal is closed, open it and hide controls bar
                consoleWindow.Show();
                ConsoleControlsBar.Visibility = Visibility.Collapsed;
            }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            // Block if update overlay is visible
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

            // Create a new ToolTip if it doesn't exist, or update the existing one
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
            
            // Set properties explicitly to ensure correct behavior
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
            
            // Force immediate visual update of ToolTip
            if (DisplayFAB.ToolTip is ToolTip tt)
            {
                // Force immediate close and reopen to update text on screen
                tt.IsOpen = false;
                tt.IsOpen = true;
            }
            
            // Bring Butterfly to front after showing windows
            if (_gameWindowsVisible)
            {
                this.Topmost = true;
                this.Activate();
                // Force UI update immediately
                this.UpdateLayout();

                // Hide Topmost only after a pause, to give Windows time to render
                Dispatcher.BeginInvoke(new Action(() => 
                { 
                    this.Topmost = false; 
                    this.Focus(); 
                }), DispatcherPriority.Render);
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Capture F5 for global refresh (same logic as refresh button)
            if (e.Key == Key.F5)
            {
                // Prevent other controls from processing F5
                e.Handled = true;
                
                // Call unified refresh method
                // checkStatusSemaphore already protects against simultaneous executions
                RefreshOnce();
            }
        }

        private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            var textBox = sender as TextBox;
            if (textBox == null) return;

            // If ConsoleWindow is open, don't process here (it processes commands)
            if (consoleWindow != null && consoleWindow.IsVisible)
            {
                return;
            }

            // Process commands directly in MainWindow when ConsoleWindow is closed
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var commandText = textBox.Text.Trim();
                var command = commandText.ToLower();

                if (!string.IsNullOrEmpty(command))
                {
                    // Echo the command
                    LogToConsole($"> {commandText}", "INFO");

                    // Process command
                    ProcessConsoleCommand(command, commandText);

                    // Clear input and maintain focus
                    textBox.Text = "";
                    textBox.Focus();
                }
            }
        }

        private void ConsoleInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Method kept for compatibility, but no longer used after replacing input with TextBlock
            // TextBlock txtLastLog doesn't need this handler
        }

        private void ProcessConsoleCommand(string command, string originalCommand)
        {
            // Separate command and arguments
            var parts = originalCommand.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var commandName = parts[0].ToLower();
            var arguments = parts.Length > 1 ? parts[1] : string.Empty;

            // Command dictionary
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
                // Buscar todos os processos MixMaster
                Process[] mixMasterProcesses = Process.GetProcessesByName("MixMaster");
                
                if (mixMasterProcesses.Length == 0)
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_NoMixMasterWindow"), "INFO");
                    return;
                }

                int totalWindowsAffected = 0;
                
                // Apply opposite state to current: if visible, hide; if hidden, show
                bool show = !_gameWindowsVisible;
                
                // For each process, hide/show all windows
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

                // Invert state after applying using the setter (which notifies automatically)
                IsGameVisible = !_gameWindowsVisible;

                // Exibir log
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
                // Fade out Play, Fade in Pause
                var playFadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                var pauseFadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };

                PlayIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, playFadeOut);
                PauseIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, pauseFadeIn);
            }
            else
            {
                // Fade out Pause, Fade in Play
                var pauseFadeOut = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
                var playFadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };

                PauseIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, pauseFadeOut);
                PlayIcon.BeginAnimation(System.Windows.Shapes.Path.OpacityProperty, playFadeIn);
            }
        }

        private void StartAutoRefreshTimer()
        {
            // Concurrency protection: ensure only one initialization occurs at a time
            lock (autoRefreshLock)
            {
                // Cancel previous loop if exists
                try
                {
                    autoRefreshCancellationTokenSource?.Cancel();
                }
                catch
                {
                    // Ignore errors when canceling old token
                }
                
                try
                {
                    autoRefreshCancellationTokenSource?.Dispose();
                }
                catch
                {
                    // Ignore errors when disposing old token
                }
                
                // Create new token for the loop
                autoRefreshCancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = autoRefreshCancellationTokenSource.Token;
                
                // Start background loop that ensures: Request -> Await Response -> Await Interval -> Next Request
                // Use while loop instead of Timer to avoid overlapping requests
                _ = Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested && isPlaying)
                    {
                        try
                        {
                            // PAUSE monitoring while an account is logging in/activating
                            // Use simple check: if connecting, skip this iteration
                            bool connecting = false;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                connecting = isConnecting;
                            });
                            
                            if (connecting)
                            {
                                // If an account is connecting, skip this iteration and wait for normal delay
                                // Loop will continue in next iteration checking again
                                // Wait for normal delay and try again in next iteration
                                await Task.Delay(15000, cancellationToken);
                                continue;
                            }
                            
                            // VISUAL SIGNALING: Change status to "Checking..." (yellow) BEFORE HTTP request
                            // This ensures immediate visual feedback to user
                            await Dispatcher.InvokeAsync(() =>
                            {
                                // Change status of SELECTED server to "Checking..." (yellow)
                                if (selectedServer != null)
                                {
                                    selectedServer.Status = "Checking...";
                                }
                                
                                // Change only accounts with Online or Offline status to "Checking..."
                                // Idle characters (gray) should not change to yellow during monitoring
                                foreach (var account in viewModel.Accounts)
                                {
                                    if (account.Status == "Online" || account.Status == "Offline")
                                    {
                                        account.Status = "Checking...";
                                    }
                                }
                            });
                            
                            // Execute status check and AWAIT complete conclusion
                            // This ensures previous request finished before starting the next one
                            // CheckAllServersAndAccountsStatus method will make HTTP request and update statuses
                            await CheckAllServersAndAccountsStatus();
                            
                            // Wait 15 seconds AFTER previous request completion
                            // This delay is ALWAYS executed, ensuring consistent interval
                            // Token cancellation will throw OperationCanceledException which will be caught
                            await Task.Delay(15000, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // Loop was canceled (server change or pause) - exit silently
                            // This exception is expected and should not be logged
                            // TaskCanceledException inherits from OperationCanceledException, so it's caught here too
                            break;
                        }
                        catch (Exception ex)
                        {
                            // In case of unexpected error, log and wait before trying again
                            try
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorMonitoringLoop", ex.Message), "ERROR");
                                });
                            }
                            catch
                            {
                                // If unable to log, continue silently
                            }
                            
                            // Aguardar antes de tentar novamente, mas verificar se ainda deve continuar
                            try
                            {
                                await Task.Delay(15000, cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                // Se o delay foi cancelado, sair normalmente
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
                // Desabilitar/habilitar ContextMenu do Border principal
                if (this.FindName("MainBorder") is Border mainBorder && mainBorder.ContextMenu != null)
                {
                    mainBorder.ContextMenu.IsEnabled = enabled;
                }

                // Desabilitar/habilitar ContextMenu da �rea vazia da ListBox
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

            // Don't select any server automatically
            // User must click on server to select it
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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkTitleBar();
        }

        private void PositionConsoleWindow()
        {
            if (consoleWindow != null)
            {
                // Posicionar console � direita da janela principal (mesmo topo)
                consoleWindow.Left = this.Left + this.Width + 10; // 10px de espa�amento
                consoleWindow.Top = this.Top;
                consoleWindow.Width = this.Width;
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            // N�o mover o console junto com a janela principal
            // PositionConsoleWindow(); <- removido
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            // N�o redimensionar o console junto com a janela principal
            // PositionConsoleWindow(); <- removido
        }

        // LoadAccounts removido - n�o usamos mais AutoReconnect.txt

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
            // Parar timer de refresh autom�tico
            autoRefreshCancellationTokenSource?.Cancel();
            autoRefreshCancellationTokenSource?.Dispose();
            autoRefreshCancellationTokenSource = null;

            // Fechar janela do console
            if (consoleWindow != null)
            {
                consoleWindow.Close();
            }

            // Salvar contas automaticamente ao fechar
            SaveAccountsAutomatically();
            
            // Fechar logger
            loggerHelper?.Dispose();
        }

        // ========== UPDATE OVERLAY ==========

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
                // If icon fails to load, just continue without it
            }
        }

        private void UpdateOverlayStatus(string cleanMessage)
        {
            Dispatcher.Invoke(() =>
            {
                if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
                {
                    // Only display clean messages (without numbers/bytes/technical logs)
                    // Filter messages that contain complex numbers, bytes, or technical information
                    if (cleanMessage.Contains("bytes") || cleanMessage.Contains("Bytes") ||
                        cleanMessage.Contains("File size") || cleanMessage.Contains("Directory does not exist") ||
                        cleanMessage.Contains("Permission denied") || cleanMessage.Contains("Directory not found") ||
                        cleanMessage.Contains("File I/O error") || cleanMessage.Contains("Network error") ||
                        cleanMessage.Contains("Download timeout") || cleanMessage.Contains("TIP:") ||
                        System.Text.RegularExpressions.Regex.IsMatch(cleanMessage, @"\d+\s*(bytes|Bytes|MB|KB|GB)"))
                    {
                        // Technical messages are not displayed in overlay - only in console
                        return;
                    }
                    
                    UpdateStatusText.Text = cleanMessage;
                }
            });
        }

        // ========== CONSOLE LOGGING ==========

        private void LogToConsole(string message, string level = "INFO")
        {
            if (consoleWindow == null) return;

            // Use LoggerHelper to format message
            string formattedMessage = LoggerHelper.FormatConsoleMessage(message, level);
            string newLine = formattedMessage + "\n";

            consoleWindow.AppendLog(newLine);
            
            // Update TextBlock with most recent complete line (thread-safe)
            // Also update overlay if visible
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (txtLastLog != null)
                {
                    // Remove timestamp (e.g., 02/25/2026 14:34:02 or 25/02/2026 14:34:02 or 2026/02/25 14:34:02) from formatted message
                    // Pattern matches: date format (any combination of numbers and slashes) followed by time format (HH:mm:ss) followed by optional space
                    string displayText = System.Text.RegularExpressions.Regex.Replace(
                        formattedMessage, 
                        @"[\d/]+\s+\d{2}:\d{2}:\d{2}\s+", 
                        string.Empty
                    );
                    
                    // Default color
                    var defaultColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF"));
                    
                    // Display message with default color
                    txtLastLog.Text = displayText;
                    txtLastLog.Foreground = defaultColor;
                }
                
                // If overlay is visible and it's an update-related log, update overlay
                if (UpdateOverlay != null && UpdateOverlay.Visibility == Visibility.Visible)
                {
                    if (message.Contains("Download") || message.Contains("update") || message.Contains("Update") || 
                        message.Contains("complete") || message.Contains("Preparing") || message.Contains("failed"))
                    {
                        // Pass original message (without formatting/timestamp) to keep UI clean
                        UpdateOverlayStatus(message);
                    }
                }
            }));
            
            // Also write to log file using LoggerHelper
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
                // CRITICAL CHECK: Prevent request if login is in progress
                if (isConnecting)
                {
                    return null;
                }

                // Use GameApiService to fetch ranking data
                // Request and success logs are handled by GameApiService
                var cache = await gameApiService.FetchRankingDataAsync(rankingUrl, serverName);
                
                return cache;
            }
            catch
            {
                // Errors are already handled by GameApiService
                return null;
            }
        }

        private async Task<bool?> CheckCharacterOnline(string characterName, bool forceRefresh = false)
        {
            // Se forceRefresh � true, invalidar cache
            if (forceRefresh)
            {
                rankingCache = null;
            }

            // Usar cache se dispon�vel e recente (menos de 60 segundos)
            if (rankingCache != null && 
                (DateTime.Now - rankingCache.LastUpdate).TotalSeconds < 60)
            {
                if (rankingCache.CharacterStatus.ContainsKey(characterName))
                {
                    return rankingCache.CharacterStatus[characterName];
                }
                else
                {
                    // Personagem n�o est� no ranking = cinza
                    return null;
                }
            }

            // Caso contr�rio, buscar novos dados
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
                        // Personagem n�o est� no ranking = cinza
                        return null;
                    }
                }
            }

            // If unable to fetch data, return null (gray) instead of false (red)
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

                    // Atualizar cache global se for o servidor selecionado
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
                // Errors are already handled by GameApiService
                server.Status = "Offline";
                server.OnlinePlayers = 0;
            }
        }

        private async Task CheckAllServersAndAccountsStatus()
        {
            // PREVENT requests during login process
            if (isConnecting)
            {
                return;
            }
            
            await checkStatusSemaphore.WaitAsync();
            try
            {
                // OPTIMIZATION: Make ONE SINGLE request to fetch ALL data
                if (selectedServer != null && !string.IsNullOrEmpty(selectedServer.RankingUrl))
                {
                    // Request log before making HTTP request
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CheckingServerStatus", selectedServer.Name), "REQUEST");
                    
                    // Fetch server data (already fills rankingCache with ALL characters)
                    await CheckServerStatus(selectedServer);
                }

                // OPTIMIZATION: Use ONLY local cache - DON'T make new requests
                // All data was already loaded in the request above
                var accountsCopy = viewModel.Accounts.ToList();
                foreach (var account in accountsCopy)
                {
                    // Fetch status directly from cache, without making new requests
                    if (rankingCache != null && rankingCache.CharacterStatus.ContainsKey(account.Character))
                    {
                        bool isOnline = rankingCache.CharacterStatus[account.Character];
                        account.Status = isOnline ? "Online" : "Offline";
                        
                        // Atualizar Level e Experience do cache
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
                        // Character is not in ranking = gray
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
            
            // AFTER updating status, check if any account needs reconnection
            await CheckAndStartReconnectionIfNeeded();
        }
        
        private async Task CheckAndStartReconnectionIfNeeded()
        {
            // STATUS PRIORITY: Check API status first, regardless of whether process is open
            var accountsCopy = await Dispatcher.InvokeAsync(() => viewModel.Accounts.ToList());
            
            foreach (var account in accountsCopy)
            {
                // STEP 1: Check character status via API
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
                
                // STEP 2: If character is OFFLINE in API + bot active + not reconnecting
                if (accountStatus == "Offline" && isBotActive && !isReconnecting)
                {
                    // If there's a process running, force termination
                    if (gameProcessId > 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ExitingGameWindow"), characterName);
                        });
                        
                        // Force terminate process
                        await ForceKillGameProcess(account, gameProcessId);
                        
                        // Clear process variables
                        await Dispatcher.InvokeAsync(() =>
                        {
                            account.GameProcessId = 0;
                        });
                        
                        // Loading interval: wait 5 seconds before restarting
                        await Task.Delay(5000);
                    }
                    
                    // Start immediate reconnection (process was already killed and cleaned, if it existed)
                    _ = Task.Run(() => AutoReconnectLoop(account));
                }
            }
        }

        private async Task ForceKillGameProcess(Account account, int processId)
        {
            try
            {
                // Try to get the process and force terminate it
                var process = Process.GetProcessById(processId);
                process.Refresh();
                
                // If process still exists, force kill it
                if (!process.HasExited)
                {
                    process.Kill();
                    // Wait a bit to ensure process was terminated
                    await Task.Delay(500);
                }
            }
            catch (ArgumentException)
            {
                // Process no longer exists - that's fine, continue
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Error accessing process (access denied, etc) - try again with more force
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
                    // If still fails, continue silently
                }
            }
            catch
            {
                // Any other error - continue silently
            }
        }

        private async Task CheckAllAccountsStatus(bool forceRefresh = false)
        {
            await checkStatusSemaphore.WaitAsync();
            try
            {
                // If forceRefresh, invalidate cache to force update
                if (forceRefresh)
                {
                    rankingCache = null;
                    
                    // Put only Online/Offline accounts in "Checking..." before fetching
                    // Idle characters (gray) should not change to yellow
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

                // Update cache of selected server if necessary
                if (selectedServer != null && !string.IsNullOrEmpty(selectedServer.RankingUrl))
                {
                    if (rankingCache == null || (DateTime.Now - rankingCache.LastUpdate).TotalSeconds > 60)
                    {
                        // OPTIMIZATION: Make ONE SINGLE request to fetch all data
                        var cache = await FetchRankingData(selectedServer.RankingUrl, selectedServer.Name);
                        if (cache != null)
                        {
                            rankingCache = cache;
                            selectedServer.OnlinePlayers = cache.OnlineCount;
                        }
                    }
                }

                // OPTIMIZATION: Check all accounts using ONLY local cache
                // Doesn't make additional requests - uses only already loaded data
                var accountsCopy = viewModel.Accounts.ToList();
                foreach (var account in accountsCopy)
                {
                    // Fetch status directly from cache, without making new requests
                    if (rankingCache != null && rankingCache.CharacterStatus.ContainsKey(account.Character))
                    {
                        bool isOnline = rankingCache.CharacterStatus[account.Character];
                        account.Status = isOnline ? "Online" : "Offline";
                        
                        // Atualizar Level e Experience do cache
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
                        // Character is not in ranking = gray
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
            
            // Ensure reconnections are checked after status update
            await CheckAndStartReconnectionIfNeeded();
        }

        // SaveAccounts n�o salva mais em AutoReconnect.txt automaticamente

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

                // Check status for new account only if playing
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
            // Can be used for future actions when an account is selected
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Deselect when clicking on empty area
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
                // Tentar obter atrav�s do Tag primeiro
                var account = menuItem.Tag as Account;

                // Se n�o funcionar, tentar atrav�s do PlacementTarget
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
            // Check if there is a selected server
            if (selectedServer == null)
            {
                LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CannotRefreshNoServer"), "INFO");
                return;
            }

            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                // Try to get through Tag first
                var account = menuItem.Tag as Account;

                // If it doesn't work, try through PlacementTarget
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
            // Check if there is a selected server
            if (selectedServer == null)
            {
                return false;
            }

            // CHECK: If already connecting (login in progress), don't make request
            // This avoids redundant request during activation
            if (isConnecting)
            {
                return false;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                // Only change to "Checking..." if current status is Online or Offline
                // Idle characters (gray) should not change to yellow
                if (account.Status == "Online" || account.Status == "Offline")
                {
                    account.Status = "Checking...";
                }
            });
            
            // Force refresh by invalidating cache
            bool? isOnline = await CheckCharacterOnline(account.Character, forceRefresh: true);
            
            await Dispatcher.InvokeAsync(() =>
            {
                account.Status = isOnline == null ? "Idle" : (isOnline.Value ? "Online" : "Offline");
            });
            
            // AFTER updating status, check if reconnection needs to be started
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
                // Start automatic reconnection
                _ = Task.Run(() => AutoReconnectLoop(account));
            }
            
            return isOnline == true;
        }

        private void RefreshAll_Click(object sender, RoutedEventArgs e)
        {
            // Use the same method as terminal refresh button for identical behavior
            RefreshOnce();
        }

        private void SaveAccountsToFile_Click(object sender, RoutedEventArgs e)
        {
            // Bloquear se ainda estiver carregando
            if (!isInitialLoadComplete)
                return;

            try
            {
                // Define path for accs folder inside .Butterfly
                var accsFolder = Path.Combine(App.RealExecutablePath, ".Butterfly", "accs");
                if (!Directory.Exists(accsFolder))
                {
                    var directoryInfo = Directory.CreateDirectory(accsFolder);
                    // Make .Butterfly folder hidden if not already
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
            // Bloquear se ainda estiver carregando
            if (!isInitialLoadComplete)
                return;

            try
            {
                // Define path for accs folder inside .Butterfly
                var accsFolder = Path.Combine(App.RealExecutablePath, ".Butterfly", "accs");
                if (!Directory.Exists(accsFolder))
                {
                    var directoryInfo = Directory.CreateDirectory(accsFolder);
                    // Make .Butterfly folder hidden if not already
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

                                // Only check status if in play mode
                                if (isPlaying)
                                {
                                    // Change only accounts with Online or Offline status to "Checking..."
                                    // Idle characters (gray) should not change to yellow during monitoring
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
            // Bloquear se ainda estiver carregando
            if (!isInitialLoadComplete)
                return;

            var count = viewModel.Accounts.Count;
            viewModel.Accounts.Clear();
            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AllAccountsCleared", count), "INFO");
        }

        private void EmptyListBox_Click(object sender, MouseButtonEventArgs e)
        {
            // Bloquear se ainda estiver carregando
            if (!isInitialLoadComplete)
                return;

            // Adicionar conta quando clicar na �rea vazia (s� quando n�o tem contas)
            if (viewModel.Accounts.Count == 0)
            {
                AddAccountFromContext_Click(sender, new RoutedEventArgs());
            }
        }

        private void AddAccountFromContext_Click(object sender, RoutedEventArgs e)
        {
            // Bloquear se ainda estiver carregando
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

            // Focar no campo CHARACTER ap�s a UI ser atualizada
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FocusCharacterField(newAccount);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private void FocusCharacterField(Account account)
        {
            // Find corresponding ListBoxItem
            var listBoxItem = AccountsGrid.ItemContainerGenerator.ContainerFromItem(account) as ListBoxItem;
            if (listBoxItem != null)
            {
                // Find CHARACTER TextBox inside ListBoxItem
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
                    // Check if all fields are filled
                    if (!string.IsNullOrWhiteSpace(account.Username) &&
                        !string.IsNullOrWhiteSpace(account.Password) &&
                        !string.IsNullOrWhiteSpace(account.Character))
                    {
                        // Finish editing
                        account.IsEditing = false;
                        
                        // Only do checking if in play mode
                        if (isPlaying)
                        {
                            // Only change to "Checking..." if current status is Online or Offline
                            // Idle characters (gray) should not change to yellow
                            if (account.Status == "Online" || account.Status == "Offline")
                            {
                                account.Status = "Checking...";
                            }

                            // Check status
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
            // Impede que o ListBoxItem capture o clique
            e.Handled = true;

            // Force ToggleButton state change
            if (sender is System.Windows.Controls.Primitives.ToggleButton toggleButton)
            {
                toggleButton.IsChecked = !toggleButton.IsChecked;
                
                // Process activate/deactivate bot action
                if (toggleButton.DataContext is Account account)
                {
                    if (toggleButton.IsChecked == true)
                    {
                        // Activate bot - Check status before connecting
                        _ = Task.Run(async () => await ActivateBotForAccount(account));
                    }
                    else
                    {
                        // Deactivate bot - Stop bot (does NOT close game)
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_BotDisabled", account.Character), "INFO");
                        // IsBotActive was already changed by ToggleButton binding
                        // This will stop AutoReconnectLoop automatically
                    }
                }
            }
        }
        
        private async Task ActivateBotForAccount(Account account)
        {
            try
            {
                // Immediate activation log - always displayed when button is clicked
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_BotEnabled", account.Character), "INFO");
                });
                
                // FIRST: Check global monitoring cache before making any request
                // Use data from last ranking request
                bool? cachedStatus = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    // Check if there's valid cache and if character is in list
                    if (rankingCache != null && 
                        (DateTime.Now - rankingCache.LastUpdate).TotalSeconds < 60 &&
                        rankingCache.CharacterStatus.ContainsKey(account.Character))
                    {
                        cachedStatus = rankingCache.CharacterStatus[account.Character];
                    }
                });
                
                // If character is online in cache, skip activation silently
                if (cachedStatus == true)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Activate global monitoring if not already active
                        if (!isPlaying)
                        {
                            Play();
                        }
                    });
                    return; // Exit without starting login process (no log)
                }
                
                // If we got here, character is not online or not in cache
                // Set connection flag to prevent global requests during login
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = true;
                });
                
                // Do individual check (may make request if necessary)
                bool isOnline = await RefreshIndividualAccount(account);
                
                if (isOnline)
                {
                    // Character is already online after check, just activate bot and start global monitoring
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Activate global monitoring if not already active
                        if (!isPlaying)
                        {
                            Play();
                        }
                        // Reset flag immediately since there's no login process
                        isConnecting = false;
                    });
                    return; // Exit silently without log
                }
                else
                {
                    // Character offline, check if needs to connect
                    int processId = 0;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        processId = account.GameProcessId;
                    });
                    
                    if (processId == 0)
                    {
                        // Game is not running, start it
                        // isConnecting will be reset at end of SelectServerInGame (success or error)
                        await LaunchGameForAccount(account);
                    }
                    else
                    {
                        // Game is running but character offline (loading?)
                        await Dispatcher.InvokeAsync(() =>
                        {
                            // Reset flag since there's no active login process
                            isConnecting = false;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false; // Reset flag in case of error
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorActivatingBot", account.Character, ex.Message), "ERROR");
                });
            }
            finally
            {
                // ENSURE flag is reset MANDATORILY at end of process
                // This finally ensures monitoring is always released
                await Dispatcher.InvokeAsync(() =>
                {
                    // Check if game really connected before resetting
                    int processId = account.GameProcessId;
                    if (processId == 0)
                    {
                        // Game didn't connect or process terminated, reset flag
                        isConnecting = false;
                    }
                    else if (isConnecting)
                    {
                        // If still connecting but process exists, login may not have completed yet
                        // But to avoid deadlock, we'll reset after a while
                        // In practice, SelectServerInGame should have already reset, but we ensure here
                        isConnecting = false;
                    }
                });
            }
        }
        
        private async Task LaunchGameForAccount(Account account)
        {
            // SEMAPHORE AT ABSOLUTE START: Ensure only one account opens process at a time
            // Check if another account is already using semaphore (for conditional log)
            bool isWaiting = _reconnectionSemaphore.CurrentCount == 0;
            
            if (isWaiting)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingReconnectionQueue"), account.Character);
                });
            }
            
            await _reconnectionSemaphore.WaitAsync();
            
            // Flag to control if process was started successfully
            // If yes, semaphore will be released in SelectServerInGame
            // If no, will be released here in finally
            bool processStarted = false;
            
            try
            {
                // Game folder path (relative to executable directory)
                string gameDirectory = Path.Combine(App.RealExecutablePath, GameFolderName);
                
                // Check if folder exists
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
                    return; // Exit try, finally will release semaphore
                }
                
                string mixMasterPath = Path.Combine(gameDirectory, "MixMaster.exe");
                
                // Check if MixMaster.exe exists
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
                    return; // Exit try, finally will release semaphore
                }

                // Build dynamic arguments based on selected server
                string arguments;
                if (selectedServer == null)
                {
                    // Fallback to Source arguments if no server is selected
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
                            // Origin: empty arguments for now
                            arguments = string.Empty;
                            break;
                        default:
                            // Fallback to Source arguments for unknown servers
                            arguments = $"3.40125 92.113.38.54 101 0 {account.Username} {account.Password} 1 AURORA_BR";
                            break;
                    }
                }
                
                // Start MixMaster.exe directly with arguments
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
                    return; // Exit try, finally will release semaphore
                }
                
                // Process started successfully - mark flag
                processStarted = true;
                
                // PID TRACKING: Store PID of process created specifically for this character
                int targetProcessId = process.Id;
                await Dispatcher.InvokeAsync(() =>
                {
                    account.GameProcessId = targetProcessId;
                });
                
                // WAIT ROBUSTLY FOR HANDLE: WaitForInputIdle + Refresh() loop
                // MainWindowHandle may take a few seconds to be assigned by Windows
                IntPtr gameWindow = await WaitForWindowHandleByProcessId(targetProcessId, maxAttempts: 30, delayMs: 500, account);
                
                if (gameWindow != IntPtr.Zero)
                {
                    // "WAIT FOR STABILITY" LOGIC: Only restore window once
                    Win32Service.ShowWindow(gameWindow, Win32Service.SW_RESTORE);
                }
                
                // Launcher bypass log
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_LauncherBypassed", account.Character), "");
                });
                
                // Continue with original bot logic (clicks and screen reading)
                // Semaphore will be released in SelectServerInGame after login completion
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
                    
                    // Fechar processo do jogo se existir
                    if (account.GameProcessId != 0)
                    {
                        try
                        {
                            var process = Process.GetProcessById(account.GameProcessId);
                            process.Kill();
                        }
                        catch
                        {
                            // Process no longer exists or cannot be closed
                        }
                        account.GameProcessId = 0;
                    }
                });
            }
            finally
            {
                // Release semaphore ONLY if process was NOT started successfully
                // If process was started, semaphore will be released in SelectServerInGame.finally
                if (!processStarted)
                {
                    _reconnectionSemaphore.Release();
                    
                    // Garantir que IsReconnecting seja resetado se houver erro antes de iniciar processo
                    await Dispatcher.InvokeAsync(() =>
                    {
                        account.IsReconnecting = false;
                    });
                }
            }
        }

        private string GetWindowTitle(IntPtr hWnd)
        {
            int length = Win32Service.GetWindowTextLength(hWnd);
            if (length == 0)
                return string.Empty;

            System.Text.StringBuilder builder = new System.Text.StringBuilder(length + 1);
            Win32Service.GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        private bool EnumChildWindowsCallback(IntPtr hwnd, IntPtr lParam)
        {
            GCHandle handle = GCHandle.FromIntPtr(lParam);
            if (handle.Target is List<IntPtr> controls)
            {
                controls.Add(hwnd);
            }
            return true; // Continue enumeration
        }

        /// <summary>
        /// Waits for launcher MainWindowHandle using only PID (no enumeration).
        /// RULE: Never uses FindWindow or enumeration - only process MainWindowHandle by PID.
        /// </summary>
        private async Task<IntPtr> WaitForLauncherWindowByPID(Account account, int processId, int timeoutSeconds)
        {
            // Use same robust wait function for handle
            return await WaitForWindowHandleByProcessId(processId, maxAttempts: timeoutSeconds * 2, delayMs: 500, account);
        }

        private async Task<bool> PerformAutoLoginByCoordinates(IntPtr launcherWindow, Account account)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CoordinateBasedLogin", account.Character), "INFO");
                });
                
                // Get launcher window position
                if (!Win32Service.GetWindowRect(launcherWindow, out Win32Service.RECT rect))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_FailedLauncherPosition", account.Character), "ERROR");
                    });
                    return false;
                }
                
                // RELATIVE coordinates based on default layout
                int usernameX = 380;
                int usernameY = 340;
                int passwordX = 380;
                int passwordY = 400;
                int buttonX = 380;
                int buttonY = 560;
                
                // Clicar no campo username (converter para coordenadas absolutas)
                await SendClickToWindow(launcherWindow, rect.Left + usernameX, rect.Top + usernameY);
                await Task.Delay(200);
                
                // Enviar username
                await SendTextToWindow(launcherWindow, account.Username);
                await Task.Delay(300);
                
                // Clicar no campo password
                await SendClickToWindow(launcherWindow, rect.Left + passwordX, rect.Top + passwordY);
                await Task.Delay(200);
                
                // Send password
                await SendTextToWindow(launcherWindow, account.Password);
                await Task.Delay(300);
                
                // Click PLAY button
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ClickingLoginButton", account.Character), "INFO");
                });
                
                await SendClickToWindow(launcherWindow, rect.Left + buttonX, rect.Top + buttonY);
                await Task.Delay(500);
                
                return true;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CoordinateLoginError", account.Character, ex.Message), "ERROR");
                });
                return false;
            }
        }

        private List<IntPtr> FindAllChildControls(IntPtr parent)
        {
            List<IntPtr> controls = new List<IntPtr>();
            
            // Deep recursive search in ENTIRE hierarchy
            FindAllChildControlsRecursive(parent, controls);
            
            return controls;
        }

        private void FindAllChildControlsRecursive(IntPtr parent, List<IntPtr> controls)
        {
            Win32Service.EnumChildWindows(parent, (hwnd, lParam) =>
            {
                controls.Add(hwnd);
                
                // Search recursively in this control's children too
                FindAllChildControlsRecursive(hwnd, controls);
                
                return true;
            }, IntPtr.Zero);
        }


        private async Task SetControlText(IntPtr controlHandle, string text)
        {
            if (controlHandle == IntPtr.Zero || !Win32Service.IsWindow(controlHandle))
                return;
            
            // Use SendMessage with WM_SETTEXT - more reliable than PostMessage
            // SendMessage waits until message is processed
            IntPtr textPtr = Marshal.StringToHGlobalUni(text);
            try
            {
                Win32Service.SendMessage(controlHandle, Win32Service.WM_SETTEXT, IntPtr.Zero, textPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(textPtr);
            }
            
            // Give time for control to process
            await Task.Delay(100);
        }

        private double GetDpiScale()
        {
            IntPtr hdc = Win32Service.GetDC(IntPtr.Zero);
            int dpi = Win32Service.GetDeviceCaps(hdc, Win32Service.LOGPIXELSX);
            Win32Service.ReleaseDC(IntPtr.Zero, hdc);
            return dpi / 96.0; // 96 DPI = 100% scale
        }

        /// <summary>
        /// Waits robustly for process MainWindowHandle using only PID.
        /// Uses WaitForInputIdle followed by Refresh() loop until handle is available.
        /// RULE: Never uses FindWindow or enumeration - only process MainWindowHandle by PID.
        /// </summary>
        private async Task<IntPtr> WaitForWindowHandleByProcessId(int processId, int maxAttempts = 10, int delayMs = 500, Account? account = null)
        {
            Process? process = null;
            
            try
            {
                // Get process by PID
                try
                {
                    process = Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    // Process does not exist
                    return IntPtr.Zero;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access Denied - process hung or closed incorrectly
                    // Don't try to recover, consider process dead
                    return IntPtr.Zero;
                }
                catch (InvalidOperationException)
                {
                    // Process was already disposed
                    return IntPtr.Zero;
                }

                // Wait for process to be ready (WaitForInputIdle)
                try
                {
                    if (!process.HasExited)
                    {
                        process.WaitForInputIdle(5000); // 5 second timeout
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process has no GUI or was already disposed
                    // Continue trying anyway
                }

                // Loop waiting for MainWindowHandle
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    try
                    {
                        // Always do Refresh() before reading MainWindowHandle
                        process.Refresh();

                        // Validate if process is still alive
                        if (process.HasExited)
                        {
                            return IntPtr.Zero;
                        }

                        // Check MainWindowHandle
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            // Validate that handle belongs to correct PID
                            Win32Service.GetWindowThreadProcessId(process.MainWindowHandle, out uint windowProcessId);
                            if (windowProcessId == processId && Win32Service.IsWindowVisible(process.MainWindowHandle))
                            {
                                return process.MainWindowHandle;
                            }
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Access Denied - process hung or closed incorrectly
                        return IntPtr.Zero;
                    }
                    catch (InvalidOperationException)
                    {
                        // Process was disposed
                        return IntPtr.Zero;
                    }
                    catch (ArgumentException)
                    {
                        // Process no longer exists
                        return IntPtr.Zero;
                    }

                    // Wait before next attempt
                    if (attempt < maxAttempts - 1)
                    {
                        await Task.Delay(delayMs);
                    }
                }
            }
            catch
            {
                // Any other error - return Zero
                return IntPtr.Zero;
            }

            // Timeout - handle did not become available
            // Close process if account is provided
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
                            // Access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // Process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // Process no longer exists
                        }
                        catch
                        {
                            // Any other error
                        }
                        account.GameProcessId = 0;
                    }
                });
            }
            else
            {
                // If no account provided, try to close process directly
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
                    // Process no longer exists or cannot be closed
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Gets MainWindowHandle dynamically from process by PID.
        /// Always does Refresh() and validates HasExited before returning handle.
        /// RULE: Never stores handle in static variable - always gets dynamically.
        /// </summary>
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
                    // Process does not exist
                    return IntPtr.Zero;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Access Denied - process hung or closed incorrectly
                    return IntPtr.Zero;
                }
                catch (InvalidOperationException)
                {
                    // Process was already disposed
                    return IntPtr.Zero;
                }

                // Always do Refresh() before reading properties
                process.Refresh();

                // Validate if process is still alive
                if (process.HasExited)
                {
                    return IntPtr.Zero;
                }

                // Return MainWindowHandle (may be Zero if window not created yet)
                return process.MainWindowHandle;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access Denied - process hung or closed incorrectly
                return IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                // Process was disposed
                return IntPtr.Zero;
            }
            catch (ArgumentException)
            {
                // Process no longer exists
                return IntPtr.Zero;
            }
        }


        private async Task SendClickToWindow(IntPtr hwnd, int relativeX, int relativeY)
        {
            // PROTECTION: Check if window is valid
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorInvalidHandle"), "ERROR");
                });
                return;
            }
            
            // PROTECTION: Check if window is visible
            if (!Win32Service.IsWindowVisible(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"ERROR: Game window is not visible!", "ERROR");
                });
                return;
            }
            
            // FOCUS PERSISTENCE LOOP: Try to get focus up to 3 times using ForceWindowFocus
            bool focusObtained = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr activeHandle = Win32Service.GetForegroundWindow();
                if (activeHandle == hwnd)
                {
                    focusObtained = true;
                    break; // Focus is already on correct window
                }
                
                // Try to get focus using ForceWindowFocus
                if (attempt > 0) // Log only on subsequent attempts
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"Fighting for focus... attempt {attempt + 1}", "WARNING");
                    });
                }
                
                await ForceWindowFocus(hwnd);
                await Task.Delay(200); // Additional delay to stabilize
                
                // Check again after ForceWindowFocus
                activeHandle = Win32Service.GetForegroundWindow();
                if (activeHandle == hwnd)
                {
                    focusObtained = true;
                    break;
                }
            }
            
            // Final check: if still no focus after all attempts, abort
            if (!focusObtained)
            {
                IntPtr finalActiveHandle = Win32Service.GetForegroundWindow();
                if (finalActiveHandle != hwnd)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"ERROR: Failed to obtain window focus after 3 attempts. Click aborted.", "ERROR");
                    });
                    return; // ABORT: Don't click if can't get focus
                }
            }
            
            // COORDINATE CONVERSION using ClientToScreen (coordinates relative to Client Area)
            Win32Service.POINT targetPoint = new Win32Service.POINT { X = relativeX, Y = relativeY };
            
            if (!Win32Service.ClientToScreen(hwnd, ref targetPoint))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole($"ERROR: Failed to convert client coordinates to screen coordinates!", "ERROR");
                });
                return;
            }
            
            // REALISTIC MOUSE SIMULATION: Move -> Wait -> Press -> Wait -> Release
            Win32Service.SetCursorPos(targetPoint.X, targetPoint.Y);
            await Task.Delay(50); // Wait after moving mouse
            
            // Click using mouse_event with realistic delays
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50); // Wait after pressing
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        // Function to force focus on a window (3 attempt loop) with reinforced protocol
        private async Task ForceWindowFocus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
                return;

            // REINFORCED FOCUS PROTOCOL: ShowWindow + SetForegroundWindow
            for (int attempt = 0; attempt < 3; attempt++)
            {
                // Always restore window first (even if not minimized)
                Win32Service.ShowWindow(hwnd, Win32Service.SW_RESTORE);
                
                // Focus the window
                Win32Service.SetForegroundWindow(hwnd);
                
                // VITAL DELAY: Wait 500ms for Windows and game engine to process new visual layer
                await Task.Delay(500);

                // Check if focus was obtained
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == hwnd)
                {
                    // Focus obtained successfully
                    return;
                }
            }

            // If we got here, couldn't get focus after 3 attempts
            // But we continue anyway, as Windows may still be processing
        }

        /// <summary>
        /// Sends double click to window getting handle dynamically from process by PID.
        /// RULE: Always gets handle dynamically - never uses stored handle.
        /// </summary>
        private async Task SendDoubleClickToWindow(int processId, int relativeX, int relativeY)
        {
            // Get handle dynamically from process
            IntPtr hwnd = GetWindowHandleDynamically(processId);
            
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorInvalidHandleDoubleClick", processId), "ERROR");
                });
                return;
            }

            // Validate process is still alive before interacting
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
                // Access Denied - process hung or closed incorrectly
                // Force termination and restart cycle
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
                // Process was disposed
                throw new Exception("Process died unexpectedly.");
            }
            catch (ArgumentException)
            {
                // Process no longer exists
                throw new Exception("Process died unexpectedly.");
            }

            // Call version with handle
            await SendDoubleClickToWindow(hwnd, relativeX, relativeY);
        }

        private async Task SendDoubleClickToWindow(IntPtr hwnd, int relativeX, int relativeY)
        {
            // PROTECTION: Check if window is valid
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorInvalidHandleDoubleClick2"), "ERROR");
                });
                return;
            }
            
            // PROTECTION: Check if window is visible
            if (!Win32Service.IsWindowVisible(hwnd))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorWindowNotVisible2"), "ERROR");
                });
                return;
            }
            
            // FOCUS PERSISTENCE LOOP: Try to get focus up to 3 times using ForceWindowFocus
            GetWindowThreadProcessId(hwnd, out uint targetProcessId);
            bool focusObtained = false;
            
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr activeHandle = Win32Service.GetForegroundWindow();
                GetWindowThreadProcessId(activeHandle, out uint activeProcessId);
                
                // Check if active window belongs to same PID and is correct window
                if (activeProcessId == targetProcessId && activeHandle == hwnd)
                {
                    focusObtained = true;
                    break; // Focus is already on correct window
                }
                
                // Try to get focus using ForceWindowFocus
                if (attempt > 0) // Log only on subsequent attempts
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LogToConsole($"Fighting for focus... attempt {attempt + 1}", "WARNING");
                    });
                }
                
                await ForceWindowFocus(hwnd);
                await Task.Delay(200); // Additional delay to stabilize
                
                // Check again after ForceWindowFocus
                activeHandle = Win32Service.GetForegroundWindow();
                GetWindowThreadProcessId(activeHandle, out uint newActiveProcessId);
                if (newActiveProcessId == targetProcessId && activeHandle == hwnd)
                {
                    focusObtained = true;
                    break;
                }
            }
            
            // Final check: if still no focus after all attempts, abort
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
                    return; // ABORT: Don't click if can't get focus
                }
            }
            
            // COORDINATE CONVERSION using ClientToScreen (coordinates relative to Client Area)
            Win32Service.POINT targetPoint = new Win32Service.POINT { X = relativeX, Y = relativeY };
            
            if (!Win32Service.ClientToScreen(hwnd, ref targetPoint))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorConvertCoordinates"), "ERROR");
                });
                return;
            }
            
            // COORDINATE FREEZE: Move mouse and keep still for 100ms before clicks
            Win32Service.SetCursorPos(targetPoint.X, targetPoint.Y);
            await Task.Delay(100); // Freeze coordinate before clicks
            
            // METHOD 1: Try using WM_LBUTTONDBLCLK (native double-click message)
            // Calculate relative coordinates for message (low word = X, high word = Y)
            // IMPORTANT: lParam must use coordinates relative to window, not absolute
            uint lParam = (uint)((relativeY << 16) | (relativeX & 0xFFFF));
            
            // Send double-click message directly to window
            Win32Service.PostMessage(hwnd, Win32Service.WM_LBUTTONDBLCLK, (IntPtr)0x0001, (IntPtr)lParam);
            await Task.Delay(50);
            
            // METHOD 2: As backup, simulate two clicks with realistic delays
            // Flow: Press -> Wait -> Release -> Wait -> Press -> Wait -> Release
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50); // Wait after pressing
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(75); // Delay between first and second click (optimal for double-click)
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50); // Wait after pressing again
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            
            // COORDINATE FREEZE: Keep mouse still for 100ms after clicks
            await Task.Delay(100); // Freeze coordinate after clicks
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

        /// <summary>
        /// Sends key to window using handle directly (for launcher that doesn't have processId).
        /// </summary>
        private async Task SendKeyToWindow(IntPtr hwnd, int vkCode)
        {
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
                return;
            
            // Bring window to front
            SetForegroundWindow(hwnd);
            await Task.Delay(100);
            
            // Send key
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
        }

        /// <summary>
        /// Sends key to window getting handle dynamically from process by PID.
        /// RULE: Always gets handle dynamically - never uses stored handle.
        /// </summary>
        private async Task SendKeyToWindow(int processId, int vkCode)
        {
            // Get handle dynamically from process
            IntPtr hwnd = GetWindowHandleDynamically(processId);
            
            if (hwnd == IntPtr.Zero || !Win32Service.IsWindow(hwnd))
            {
                // Invalid handle - process may have died
                return;
            }

            // Validate process is still alive before interacting
            try
            {
                var process = Process.GetProcessById(processId);
                process.Refresh();
                if (process.HasExited)
                {
                    return; // Process died
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access Denied - process hung or closed incorrectly
                // Force termination and restart cycle
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
                // Process was disposed
                throw new Exception("Process died unexpectedly.");
            }
            catch (ArgumentException)
            {
                // Process no longer exists
                throw new Exception("Process died unexpectedly.");
            }
            
            // Bring window to front
            SetForegroundWindow(hwnd);
            await Task.Delay(100);
            
            // Send key
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(hwnd, Win32Service.WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
        }

        private async Task ClickButton(IntPtr buttonHandle)
        {
            if (buttonHandle == IntPtr.Zero || !IsWindow(buttonHandle))
                return;
            
            // Simulate mouse click at center of button
            // This is more reliable than BM_CLICK in some cases
            
            // Send WM_LBUTTONDOWN and WM_LBUTTONUP directly to button
            Win32Service.PostMessage(buttonHandle, Win32Service.WM_LBUTTONDOWN, (IntPtr)0x0001, IntPtr.Zero);
            await Task.Delay(50);
            Win32Service.PostMessage(buttonHandle, Win32Service.WM_LBUTTONUP, IntPtr.Zero, IntPtr.Zero);
        }

        private async Task WaitAndMonitorGameProcess(Account account)
        {
            try
            {
                // PID TRACKING: Use ONLY the PID that was stored when process was started
                int targetProcessId = 0;
                await Dispatcher.InvokeAsync(() =>
                {
                    targetProcessId = account.GameProcessId;
                });
                
                if (targetProcessId == 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false; // Reset flag in case of error
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorProcessNotFound"), account.Character);
                        account.IsBotActive = false;
                    });
                    return;
                }
                
                // Check if process still exists
                Process? targetProcess = null;
                try
                {
                    targetProcess = Process.GetProcessById(targetProcessId);
                }
                catch (ArgumentException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false; // Reset flag in case of error
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorProcessNotFound2", targetProcessId), account.Character);
                        account.IsBotActive = false;
                    });
                    return;
                }
                
                // Select server automatically using correct PID
                await SelectServerInGame(targetProcessId, account);
                
                // Now monitor the real game process
                // DON'T do refresh or start monitoring here - that will be done after F12
                await MonitorGameProcess(targetProcessId, account);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false; // Resetar flag em caso de erro
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorWaitingGameProcess", ex.Message), "ERROR");
                });
            }
        }

        private async Task SelectServerInGame(int processId, Account account)
        {
            // Semaphore already acquired in LaunchGameForAccount
            // This method executes within semaphore context
            try
            {
                // Ensure isConnecting is true during entire login process
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = true;
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingServerScreen", account.Character), "");
                });
                
                // WAIT ROBUSTLY FOR HANDLE using only PID (no enumeration)
                // RULE: Use only process MainWindowHandle by specific PID
                IntPtr gameWindow = await WaitForWindowHandleByProcessId(processId, maxAttempts: 30, delayMs: 500, account);
                
                if (gameWindow == IntPtr.Zero)
                {
                    // STATE RESET AFTER ERROR: Clear flags to allow new attempt
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0; // Clear PID to force new search
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_GameWindowNotFound", processId), account.Character);
                        
                        // Close process if still exists (may be hung)
                        try
                        {
                            var process = Process.GetProcessById(processId);
                            process.Kill();
                        }
                        catch
                        {
                            // Process no longer exists or cannot be closed
                        }
                    });
                    return;
                }
                
                // "WAIT FOR STABILITY" LOGIC: Only restore window once
                Win32Service.ShowWindow(gameWindow, Win32Service.SW_RESTORE);
                
                // WAIT UNTIL SELECTION SCREEN APPEARS (detecting by pixels)
                // Returns (bool, IntPtr) where IntPtr is new handle if re-found
                // Passes processId for dynamic process validation
                var (screenDetected, newWindow) = await WaitForServerSelectionScreen(gameWindow, processId, account);
                
                // If found a new window, update
                if (newWindow != IntPtr.Zero)
                {
                    gameWindow = newWindow;
                }
                
                if (!screenDetected)
                {
                    // STATE RESET AFTER ERROR: Clear flags to allow new attempt
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0; // Clear PID to force new search
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ServerScreenTimeout", account.Character), "");
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ServerScreenDetected", account.Character), "");
                });
                
                // FOCUS PERSISTENCE LOOP: Try to get focus up to 3 times
                bool focusObtained = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr activeHandle = Win32Service.GetForegroundWindow();
                    GetWindowThreadProcessId(activeHandle, out uint activeProcessId);
                    
                    // Check if active window belongs to correct PID
                    if (activeProcessId == processId && activeHandle == gameWindow)
                    {
                        focusObtained = true;
                        break; // Focus is already on correct window
                    }
                    
                    // Try to get focus: ShowWindow(Restore) + SetForegroundWindow
                    if (attempt > 0) // Log only on subsequent attempts
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WarningFightingFocus", attempt + 1), account.Character);
                        });
                    }
                    
                    Win32Service.ShowWindow(gameWindow, Win32Service.SW_RESTORE);
                    Win32Service.SetForegroundWindow(gameWindow);
                    await Task.Delay(200); // Delay to stabilize
                }
                
                // Final check: if still no focus after all attempts, abort
                if (!focusObtained)
                {
                    IntPtr finalActiveHandle = Win32Service.GetForegroundWindow();
                    GetWindowThreadProcessId(finalActiveHandle, out uint finalActiveProcessId);
                    if (finalActiveProcessId != processId || finalActiveHandle != gameWindow)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ErrorFailedFocus"), account.Character);
                            // COMPLETE STATE RESET: Clear everything to allow new attempt
                            account.IsReconnecting = false;
                            account.Status = "Offline";
                            isConnecting = false;
                            
                            // Close failed game process
                            if (account.GameProcessId != 0)
                            {
                                try
                                {
                                    var process = Process.GetProcessById(account.GameProcessId);
                                    process.Kill();
                                }
                                catch
                                {
                                    // Process no longer exists or cannot be closed
                                }
                                account.GameProcessId = 0;
                            }
                        });
                        return; // ABORT: Don't click if can't get focus
                    }
                }
                
                // METHOD: Clicks by coordinates relative to window Client Area
                // Game resolution: 1024x768 in windowed mode
                // Coordinates are relative to Client Area (game rendering area)
                // SendDoubleClickToWindow automatically converts to absolute screen coordinates
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConnectingToServer", account.Character), "");
                });
                
                // Coordinates relative to window Client Area (1024x768)
                // These coordinates will be automatically converted to absolute
                int serverX = 815;
                int serverY = 310;
                
                // Robust double-click on server using specific function (with processId to get handle dynamically)
                await SendDoubleClickToWindow(processId, serverX, serverY);
                
                // WAIT FOR CHARACTER SELECTION SCREEN (automatic detection already waits)
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingCharacterScreen", account.Character), "");
                });
                
                bool characterScreenDetected = await WaitForCharacterSelectionScreen(gameWindow, processId, account);
                
                if (!characterScreenDetected)
                {
                    // STATE RESET AFTER ERROR: Clear flags to allow new attempt
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0; // Clear PID to force new search
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterScreenTimeout", account.Character), "");
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterScreenDetected", account.Character), "");
                });
                
                // Click ENTER button (coordinates relative to Client Area: 600, 654)
                // These coordinates will be automatically converted to absolute
                int enterX = 600;
                int enterY = 654;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ConnectingToCharacter", account.Character), "");
                });
                
                // Robust double-click on Enter button using specific function (with processId to get handle dynamically)
                await SendDoubleClickToWindow(processId, enterX, enterY);
                
                // WAIT TO ENTER GAME MAP
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_WaitingGameMap", account.Character), "");
                });
                
                bool mapLoaded = await WaitForGameMapLoad(gameWindow, processId, account);
                
                if (!mapLoaded)
                {
                    // STATE RESET AFTER ERROR: Clear flags to allow new attempt
                    await Dispatcher.InvokeAsync(() =>
                    {
                        isConnecting = false;
                        account.GameProcessId = 0; // Clear PID to force new search
                        LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_GameMapTimeout", account.Character), "");
                    });
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_Connected", account.Character), "");
                });
                
                // PRESS F12 TO ACTIVATE AUTOPLAY (map already loaded)
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_ActivatingAutoPlay", account.Character), "");
                });
                
                // Send F12 using processId to get handle dynamically
                await SendKeyToWindow(processId, VK_F12);
                
                await Dispatcher.InvokeAsync(() =>
                {
                    LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_AutoPlayActivated", account.Character), "");
                });
                
                // Wait 2 seconds for server to update character status in ranking
                await Task.Delay(2000);
                
                // RESET connection flag MANDATORILY after successful login
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                });
                
                // After F12 and delay, activate global monitoring (Play)
                // This ensures global monitoring takes care of all characters
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
                // COMPLETE STATE RESET AFTER ERROR: Clear everything to allow new attempt
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    account.IsReconnecting = false;
                    account.Status = "Offline";
                    
                    // Close failed game process (may have hung or died)
                    if (account.GameProcessId != 0)
                    {
                        try
                        {
                            var process = Process.GetProcessById(account.GameProcessId);
                            process.Kill();
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // Access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // Process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // Process no longer exists
                        }
                        catch
                        {
                            // Any other error
                        }
                        account.GameProcessId = 0;
                    }
                    
                    LogToConsole($"Error selecting server: {ex.Message}", "ERROR");
                });
            }
            finally
            {
                // Always release semaphore after login process completion (success or error)
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
                
                // Timeout - close process
                await Dispatcher.InvokeAsync(() =>
                {
                    isConnecting = false;
                    LogToConsole($"Timeout: Server screen not detected.", account.Character);
                    
                    // Close failed game process
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
                            // Access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // Process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // Process no longer exists
                        }
                        catch
                        {
                            // Any other error
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
                
                // Timeout - close process
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
                            // Access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // Process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // Process no longer exists
                        }
                        catch
                        {
                            // Any other error
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
                
                // Timeout - close process
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
                            // Access Denied - process already hung or was closed
                        }
                        catch (InvalidOperationException)
                        {
                            // Process was already disposed
                        }
                        catch (ArgumentException)
                        {
                            // Process no longer exists
                        }
                        catch
                        {
                            // Any other error
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
                // Check every 2 seconds if process still exists
                while (true)
                {
                    await Task.Delay(2000);
                    
                    try
                    {
                        var process = Process.GetProcessById(processId);
                        // If we got here, process still exists
                    }
                    catch (ArgumentException)
                    {
                        // Process no longer exists - just clear GameProcessId silently
                        await Dispatcher.InvokeAsync(() =>
                        {
                            account.GameProcessId = 0;
                        });
                        
                        // Reconnection will be started ONLY by monitoring/refresh detecting offline
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
                // Check if already reconnecting (protection against multiple reconnections)
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
                    // Reconnection loop already running for this account
                    return;
                }
                
                // Flag for first iteration (immediate reconnection without double-check)
                bool isFirstIteration = true;
                
                // Reconnection loop while bot is active
                while (true)
                {
                    // Check if should still reconnect
                    bool shouldContinue = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        shouldContinue = account.IsBotActive && account.GameProcessId == 0;
                    });
                    
                    if (!shouldContinue)
                    {
                        // Bot was turned off or game is already running
                        break;
                    }
                    
                    // On first iteration, skip delay and individual check (already detected offline in global return)
                    if (!isFirstIteration)
                    {
                        // Wait 1 second before trying to reconnect again (after failure)
                        await Task.Delay(1000);
                        
                        // Do individual refresh to check current status (only in subsequent iterations)
                        bool isOnline = await RefreshIndividualAccount(account);
                        
                        // If online, don't need to reconnect
                        if (isOnline)
                        {
                            break;
                        }
                        
                        // Check again if should continue (user may have turned off bot)
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
                        // First iteration: immediate reconnection log
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LogToConsole(Butterfly.Services.LocalizationManager.GetString("Log_CharacterDisconnectedStarting", characterName), "");
                        });
                        isFirstIteration = false;
                    }
                    
                    // Character is offline, reconnect
                    await LaunchGameForAccount(account);
                    
                    // Actively monitor GameProcessId with 30 second timeout
                    bool connected = await WaitForGameConnection(account, timeoutSeconds: 30);
                    
                    if (connected)
                    {
                        // Game connected successfully - WaitAndMonitorGameProcess will call Play()
                        // Wait a bit to ensure process was started
                        await Task.Delay(1000);
                        break;
                    }
                    
                    // If we got here, attempt failed, loop will repeat after delay at start
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
                // Always clear reconnection flag on exit
                await Dispatcher.InvokeAsync(() =>
                {
                    account.IsReconnecting = false;
                });
            }
        }
        
        private async Task<bool> WaitForGameConnection(Account account, int timeoutSeconds)
        {
            // Monitor GameProcessId every 500ms until timeout
            int attempts = 0;
            int maxAttempts = timeoutSeconds * 2; // 500ms per attempt
            
            while (attempts < maxAttempts)
            {
                int currentProcessId = 0;
                bool shouldContinue = false;
                
                await Dispatcher.InvokeAsync(() =>
                {
                    currentProcessId = account.GameProcessId;
                    shouldContinue = account.IsBotActive;
                });
                
                // If bot was turned off, stop waiting
                if (!shouldContinue)
                {
                    return false;
                }
                
                // If game connected, return success
                if (currentProcessId > 0)
                {
                    return true;
                }
                
                await Task.Delay(500);
                attempts++;
            }
            
            // Timeout reached
            return false;
        }

        private void AccountsGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && draggedItem == null)
            {
                var listBox = sender as ListBox;
                if (listBox == null) return;

                // Verificar se o elemento sob o mouse � um TextBox (modo de edi��o)
                var element = e.OriginalSource as DependencyObject;
                if (element != null && (element is TextBox || FindParent<TextBox>(element) != null))
                {
                    return; // N�o iniciar drag se estiver sobre um TextBox
                }

                var mousePosition = e.GetPosition(listBox);
                var item = GetItemAtPosition(listBox, mousePosition);

                if (item != null && item is Account account)
                {
                    // Prevenir drag-and-drop de items em modo de edi��o
                    if (account.IsEditing)
                        return;

                    draggedItem = account;
                    DragDrop.DoDragDrop(listBox, account, DragDropEffects.Move);
                    draggedItem = null;
                }
            }
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
            // Drag and drop completed
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
            if (ServersGrid.SelectedItem is Server server)
            {
                // Check if it's a server change (not just initial selection)
                bool isServerChange = selectedServer != null && selectedServer != server;
                
                // Unselect previous server (but keep the last status)
                if (selectedServer != null)
                {
                    selectedServer.IsSelected = false;
                    // Do NOT change status - keep the last one (Online/Offline/etc)
                }

                // Mark new server
                selectedServer = server;
                selectedServer.IsSelected = true;
                
                // Mark that a server was selected (show lightning bolts)
                viewModel.IsServerSelected = true;

                // Clear cache to force update
                rankingCache = null;

                // If monitoring is already running and there was a server change,
                // interrupt current delay and make immediate request
                if (isPlaying && isServerChange)
                {
                    // Cancel current token to safely interrupt Task.Delay
                    // StartAutoRefreshTimer already has concurrency protection and will do complete cleanup
                    try
                    {
                        autoRefreshCancellationTokenSource?.Cancel();
                    }
                    catch
                    {
                        // Ignore errors when canceling - StartAutoRefreshTimer will do cleanup
                    }
                    
                    // Wait a bit to ensure cancellation was processed
                    // But use Task.Delay without token to avoid exception if already canceled
                    await Task.Delay(100);
                    
                    // Restart loop immediately (will make request for new server)
                    // Lock in StartAutoRefreshTimer ensures no conflict
                    StartAutoRefreshTimer();
                }
                // AUTO-START: Start monitoring automatically when server is selected
                else if (!isPlaying)
                {
                    Play();
                }
            }
        }

        /// <summary>
        /// Updates the window title with the license type
        /// </summary>
        public void UpdateWindowTitle()
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateWindowTitle();
            Butterfly.Helpers.FocusHelper.ForceForeground(this);
            
            // Initialize heavy operations after window is shown (prevents blocking)
            InitializeHeavyOperations();
        }
        
        private void InitializeHeavyOperations()
        {
            // Initialize logging (moved from constructor to reduce startup time)
            loggerHelper.InitializeLogging();
            // If logFilePath is empty, logger will continue working but without file writing
            // This is intentional - app should open even without permission to create logs

            // Set initial log text
            if (txtLastLog != null)
            {
                txtLastLog.Text = Butterfly.Services.LocalizationManager.GetString("Log_WaitingForEvents");
            }
            
            // Display welcome message in console (line by line, without timestamp/level prefix)
            if (consoleWindow != null)
            {
                consoleWindow.AppendLog(Butterfly.Services.LocalizationManager.GetString("Console_Welcome_1"));
                consoleWindow.AppendLog(Butterfly.Services.LocalizationManager.GetString("Console_Welcome_2"));
                consoleWindow.AppendLog(Butterfly.Services.LocalizationManager.GetString("Console_Welcome_3"));
            }

            // Initialize servers (moved from constructor)
            InitializeServers();
            
            // Load accounts (moved from constructor)
            LoadAccountsAutomatically();

            // Start in paused mode - don't make requests
            // All accounts start with "Paused" status
            foreach (var account in viewModel.Accounts)
            {
                account.Status = "Paused";
            }

            // Servers all start in "Idle" (no initial selection)
            // User must click on a server to select it and load

            // Check for updates in background (moved from constructor to reduce startup time)
            _ = Task.Run(async () =>
            {
                try
                {
                    // bool hasUpdate = await updateService.CheckForUpdateAsync(Version);
                    bool hasUpdate = false;
                    
                    if (hasUpdate)
                    {
                        // Mandatory update - start automatically
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            LogToConsole("New version detected. Mandatory update starting...", "INFO");
                            ShowUpdateOverlay();
                            
                            try
                            {
                                // Configure progress bar for download progress
                                if (UpdateProgressBar != null)
                                {
                                    UpdateProgressBar.IsIndeterminate = false;
                                    UpdateProgressBar.Minimum = 0;
                                    UpdateProgressBar.Maximum = 100;
                                    UpdateProgressBar.Value = 0;
                                }
                                
                                // Create progress reporter that updates the UI
                                // Progress<double> automatically captures SynchronizationContext, so callback runs on UI thread
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
                                    // If download failed, show error and allow user to close app
                                    LogToConsole("Mandatory update failed. Please check your internet connection and try again.", "ERROR");
                                    LogToConsole("You can close the application and try again later.", "INFO");
                                    
                                    // Overlay remains visible to block interactions
                                    // User can close app manually
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
                    // Log verification error, but don't block the app
                    LogToConsole($"Update check failed: {ex.Message}", "ERROR");
                }
            });
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe event to prevent memory leaks
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
