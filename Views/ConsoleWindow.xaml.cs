using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
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
            
            // Ensure RichTextBox starts completely empty (no initial paragraph)
            InitializeEmptyConsole();
        }
        
        private void InitializeEmptyConsole()
        {
            // Clear any initial content and ensure document is empty
            var document = ConsoleOutput.Document;
            document.Blocks.Clear();
            
            // Remove any default empty paragraph that might exist
            // FlowDocument may create an empty paragraph by default
            if (document.Blocks.Count > 0)
            {
                document.Blocks.Clear();
            }
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
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                // Remove leading/trailing whitespace and control characters
                string cleanedMessage = message?.TrimStart('\n', '\r', '\t', ' ') ?? string.Empty;
                
                // Remove trailing newline if present (we'll add it as a paragraph break)
                cleanedMessage = cleanedMessage.TrimEnd('\n', '\r');
                
                // Skip empty messages
                if (string.IsNullOrEmpty(cleanedMessage))
                {
                    return;
                }
                
                var document = ConsoleOutput.Document;
                
                // Check if console is empty (first message)
                // FlowDocument may have an empty default paragraph, so check if it's truly empty
                bool isFirstMessage = false;
                if (document.Blocks.Count == 0)
                {
                    isFirstMessage = true;
                }
                else if (document.Blocks.Count == 1)
                {
                    var firstBlock = document.Blocks.FirstBlock;
                    if (firstBlock is Paragraph firstPara)
                    {
                        // Check if paragraph is empty (no inlines or only whitespace)
                        if (firstPara.Inlines.Count == 0)
                        {
                            isFirstMessage = true;
                        }
                        else
                        {
                            // Check if all inlines are empty or whitespace
                            bool allEmpty = true;
                            foreach (var inline in firstPara.Inlines)
                            {
                                if (inline is Run existingRun && !string.IsNullOrWhiteSpace(existingRun.Text))
                                {
                                    allEmpty = false;
                                    break;
                                }
                            }
                            isFirstMessage = allEmpty;
                        }
                    }
                    else
                    {
                        // If first block is not a paragraph, consider it empty
                        isFirstMessage = true;
                    }
                }
                
                // If console is empty, ensure it's truly empty (remove any empty paragraph)
                if (isFirstMessage)
                {
                    document.Blocks.Clear();
                }
                
                var paragraph = new Paragraph();
                paragraph.Margin = new Thickness(0);
                paragraph.Padding = new Thickness(0);
                paragraph.LineHeight = 1;
                
                // Default color
                var defaultColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"));
                
                // Add entire message with default color
                paragraph.Inlines.Add(new Run(cleanedMessage) { Foreground = defaultColor });
                
                document.Blocks.Add(paragraph);
                ConsoleScrollViewer.ScrollToEnd();
            }));
        }

        public void ClearLog()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ConsoleOutput.Document.Blocks.Clear();
                var defaultColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCCCCC"));
                var paragraph = new Paragraph();
                paragraph.Margin = new Thickness(0);
                paragraph.Padding = new Thickness(0);
                paragraph.LineHeight = 1;
                var run = new Run("Console cleared. Thanks for using Butterfly! 🦋") { Foreground = defaultColor };
                paragraph.Inlines.Add(run);
                ConsoleOutput.Document.Blocks.Add(paragraph);
                ConsoleScrollViewer.ScrollToEnd();
            }));
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
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ConsoleOutput.Document.Blocks.Clear();
            }));
            AppendLog("Console cleared. Thanks for using Butterfly! 🦋\n");
        }

        private void ExecuteHelpCommand()
        {
            AppendLog("clear - Clear the console\n");
            AppendLog("logs - Open the logs folder\n");
            AppendLog("display - Toggle visibility of all game windows\n");
            AppendLog("help - Show this menu\n");
            AppendLog("exit - Closes the application\n");
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
