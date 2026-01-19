using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Butterfly.Native;

namespace Butterfly.Views
{
    public partial class ConsoleWindow : Window
    {
        private bool allowClose = false;
        private bool isPlaying = false;
        private MainWindow? mainWindow;
        private System.Collections.Generic.List<string> commandHistory = new System.Collections.Generic.List<string>();
        private int commandHistoryIndex = -1;

        public ConsoleWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            this.mainWindow = mainWindow;
            this.Loaded += ConsoleWindow_Loaded;
            this.Activated += ConsoleWindow_Activated;
            this.GotFocus += ConsoleWindow_GotFocus;
            this.Unloaded += ConsoleWindow_Unloaded;
            
            // Subscribe to LicenseType change event
            App.OnLicenseTypeChanged += ConsoleWindow_OnLicenseTypeChanged;
            
            // Update title when window loads
            UpdateWindowTitle();
        }

        private void ConsoleWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDarkTitleBar();
            ConsoleInput.Focus();
            UpdateWindowTitle();
        }

        private void ConsoleWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            // Unsubscribe event to prevent memory leaks
            App.OnLicenseTypeChanged -= ConsoleWindow_OnLicenseTypeChanged;
        }

        private void ConsoleWindow_OnLicenseTypeChanged(string newTier)
        {
            Dispatcher.BeginInvoke(new Action(() => {
                UpdateWindowTitle();
            }));
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

        private void ConsoleWindow_Activated(object? sender, EventArgs e)
        {
            if (!ConsoleOutput.IsFocused)
            {
                ConsoleInput.Focus();
            }
        }

        private void ConsoleWindow_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!ConsoleInput.IsFocused && !ConsoleOutput.IsFocused)
            {
                ConsoleInput.Focus();
            }
        }

        private void ApplyDarkTitleBar()
        {
            var windowHelper = new WindowInteropHelper(this);
            var hwnd = windowHelper.Handle;

            if (Environment.OSVersion.Version.Major >= 10 && hwnd != IntPtr.Zero)
            {
                int useImmersiveDarkMode = 1;

                if (Butterfly.Native.Win32Service.DwmSetWindowAttribute(hwnd, Butterfly.Native.Win32Service.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
                {
                    Butterfly.Native.Win32Service.DwmSetWindowAttribute(hwnd, Butterfly.Native.Win32Service.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
                }
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyDarkTitleBar();
        }

        public void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ConsoleOutput.Text += message;
                ConsoleScrollViewer.ScrollToEnd();
            });
        }

        public void ClearLog()
        {
            Dispatcher.Invoke(() =>
            {
                ConsoleOutput.Text = "Console cleared. Thanks for using Butterfly! 🦋\n";
                ConsoleScrollViewer.ScrollToEnd();
            });
        }

        private void ConsoleInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var textBox = sender as System.Windows.Controls.TextBox;
            if (textBox == null) return;

            if (e.Key == System.Windows.Input.Key.Up)
            {
                e.Handled = true;
                if (commandHistory.Count > 0)
                {
                    if (commandHistoryIndex < 0)
                    {
                        commandHistoryIndex = commandHistory.Count - 1;
                    }
                    else if (commandHistoryIndex > 0)
                    {
                        commandHistoryIndex--;
                    }
                    textBox.Text = commandHistory[commandHistoryIndex];
                    textBox.CaretIndex = textBox.Text.Length;
                }
                return;
            }

            if (e.Key == System.Windows.Input.Key.Down)
            {
                e.Handled = true;
                if (commandHistory.Count > 0 && commandHistoryIndex >= 0)
                {
                    if (commandHistoryIndex < commandHistory.Count - 1)
                    {
                        commandHistoryIndex++;
                        textBox.Text = commandHistory[commandHistoryIndex];
                    }
                    else
                    {
                        commandHistoryIndex = -1;
                        textBox.Text = "";
                    }
                    textBox.CaretIndex = textBox.Text.Length;
                }
                return;
            }

            if (e.Key != System.Windows.Input.Key.Enter)
            {
                commandHistoryIndex = -1;
            }

            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                var commandText = textBox.Text.Trim();
                var command = commandText.ToLower();

                if (!string.IsNullOrEmpty(command))
                {
                    if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != commandText)
                    {
                        commandHistory.Add(commandText);
                        if (commandHistory.Count > 50)
                        {
                            commandHistory.RemoveAt(0);
                        }
                    }

                    AppendLog($"> {commandText}\n");

                    ProcessCommand(command, commandText);

                    commandHistoryIndex = -1;
                }

                textBox.Text = "";
                textBox.Focus();
            }
        }

        private void ProcessCommand(string command, string originalCommand)
        {
            var parts = originalCommand.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var commandName = parts[0].ToLower();
            var arguments = parts.Length > 1 ? parts[1] : string.Empty;

            var commandHandlers = new System.Collections.Generic.Dictionary<string, System.Action<string>>
            {
                { "cls", (args) => ExecuteClearCommand() },
                { "clear", (args) => ExecuteClearCommand() },
                { "help", (args) => ExecuteHelpCommand() },
                { "stats", (args) => ExecuteStatsCommand() },
                { "reconnect", (args) => ExecuteReconnectCommand(args) },
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
                AppendLog($"Unknown command: '{originalCommand}'. Type 'help' for available commands.\n");
            }

            Dispatcher.Invoke(() =>
            {
                ConsoleInput.Focus();
            });
        }

        private void ExecuteClearCommand()
        {
            ConsoleOutput.Text = "";
            AppendLog("Console cleared. Thanks for using Butterfly! 🦋\n");
        }

        private void ExecuteHelpCommand()
        {
            AppendLog("Available commands:\n");
            AppendLog("  cls, clear      - Clear the console\n");
            AppendLog("  stats           - Show accounts current stats\n");
            AppendLog("  reconnect <n>   - Reconnect a single account\n");
            AppendLog("  logs            - Open the logs folder\n");
            AppendLog("  display         - Toggle visibility of all game windows\n");
            AppendLog("  help            - Show this menu\n");
            AppendLog("  exit            - Closes the application\n");
        }

        private void ExecuteToggleGameWindowsCommand()
        {
            if (mainWindow == null)
            {
                AppendLog("Error: MainWindow not available.\n");
                return;
            }

            mainWindow.ToggleGameWindows();
        }

        private void ExecuteStatsCommand()
        {
            if (mainWindow == null)
            {
                AppendLog("Error: MainWindow not available.\n");
                return;
            }

            var stats = mainWindow.GetAccountStats();
            if (stats == null)
            {
                AppendLog("Error: Could not retrieve account statistics.\n");
                return;
            }

            var accounts = mainWindow.GetAllAccounts();
            if (accounts != null && accounts.Count > 0)
            {
                AppendLog($"\n=== Characters Status ===\n");
                foreach (var account in accounts)
                {
                    string status = account.Status;
                    if (status == "Online")
                    {
                        AppendLog($"  {account.Character}: Online\n");
                    }
                    else if (status == "Offline")
                    {
                        AppendLog($"  {account.Character}: Offline\n");
                    }
                    else
                    {
                        AppendLog($"  {account.Character}: {status}\n");
                    }
                }
                AppendLog($"==========================\n\n");
            }
            else
            {
                AppendLog("No characters found.\n");
            }
        }

        private void ExecuteReconnectCommand(string characterName)
        {
            if (mainWindow == null)
            {
                AppendLog("Error: MainWindow not available.\n");
                return;
            }

            if (string.IsNullOrWhiteSpace(characterName))
            {
                AppendLog("Usage: reconnect <character_name>\n");
                return;
            }

            var success = mainWindow.ReconnectCharacter(characterName.Trim());
            if (success)
            {
                AppendLog($"Reconnection initiated for '{characterName}'.\n");
            }
            else
            {
                AppendLog($"Character '{characterName}' not found.\n");
            }
        }

        private void ExecuteLogsCommand()
        {
            if (mainWindow == null)
            {
                AppendLog("Error: MainWindow not available.\n");
                return;
            }

            var logsPath = mainWindow.GetLogsFolderPath();
            if (string.IsNullOrEmpty(logsPath))
            {
                AppendLog("Error: Logs folder path not available.\n");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", logsPath);
                AppendLog($"Opened logs folder: {logsPath}\n");
            }
            catch (Exception ex)
            {
                AppendLog($"Error opening logs folder: {ex.Message}\n");
            }
        }

        private void ExecuteExitCommand()
        {
            AppendLog("[SYSTEM] Shutting down Butterfly... Goodbye!\n");
            Application.Current.Shutdown();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            isPlaying = !isPlaying;
            AnimatePlayPauseIcon();

            if (isPlaying)
            {
                mainWindow?.Play();
            }
            else
            {
                mainWindow?.Pause();
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            mainWindow?.RefreshOnce();
        }

        private void AnimatePlayPauseIcon()
        {
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

        public void UpdatePlayPauseState(bool playing)
        {
            Dispatcher.Invoke(() =>
            {
                if (isPlaying != playing)
                {
                    isPlaying = playing;
                    AnimatePlayPauseIcon();
                }
            });
        }

        public new void Close()
        {
            allowClose = true;
            base.Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!allowClose)
            {
                e.Cancel = true;
                this.Hide();
            }
            base.OnClosing(e);
        }

        private void ConsoleInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var textBox = sender as System.Windows.Controls.TextBox;
            if (textBox != null)
            {
                CustomCaret.Visibility = string.IsNullOrEmpty(textBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
