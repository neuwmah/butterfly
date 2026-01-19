using System;
using System.IO;
using Butterfly.Services;

namespace Butterfly.Helpers
{
    /// <summary>
    /// Centralized helper for application log management
    /// </summary>
    public class LoggerHelper : IDisposable
    {
        private StreamWriter? _logFileWriter;
        private string _logFilePath = string.Empty;
        private readonly object _lockObject = new object();

        /// <summary>
        /// Path to logs folder (inside .Butterfly folder)
        /// </summary>
        public static string LogsFolderPath => Path.Combine(App.RealExecutablePath, ".Butterfly", "logs");

        /// <summary>
        /// Initializes the logging system, creating the logs folder and log file
        /// </summary>
        /// <returns>Path to created log file, or empty string if it fails</returns>
        public string InitializeLogging()
        {
            try
            {
                // Create .Butterfly/logs folder if it doesn't exist (CreateDirectory creates all necessary subfolders)
                if (!Directory.Exists(LogsFolderPath))
                {
                    try
                    {
                        var directoryInfo = Directory.CreateDirectory(LogsFolderPath);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // No permission to create folder - continue without file logging
                        return string.Empty;
                    }
                    catch (IOException)
                    {
                        // I/O error (disk full, etc.) - continue without file logging
                        return string.Empty;
                    }
                    catch (Exception)
                    {
                        // Any other error - continue without file logging
                        return string.Empty;
                    }
                }

                // Try to make .Butterfly folder hidden (optional operation - should not crash the app)
                try
                {
                    string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                    if (Directory.Exists(butterflyFolder))
                    {
                        var butterflyDirInfo = new DirectoryInfo(butterflyFolder);
                        if ((butterflyDirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            // Check if we have permission before trying to change attributes
                            if (SecurityHelper.IsRunningAsAdministrator() || 
                                (butterflyDirInfo.Attributes & FileAttributes.ReadOnly) != FileAttributes.ReadOnly)
                            {
                                File.SetAttributes(butterflyFolder, butterflyDirInfo.Attributes | FileAttributes.Hidden);
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // No permission to change attributes - silently ignore
                }
                catch (IOException)
                {
                    // I/O error when changing attributes - silently ignore
                }
                catch (Exception)
                {
                    // Any other error when changing attributes - silently ignore
                }

                // File name: butterfly_PID_YYYYMMDD_HHMMSS.log (includes PID to avoid conflicts)
                int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(LogsFolderPath, $"butterfly_{processId}_{timestamp}.log");

                // Open file for writing with FileShare.ReadWrite to allow multiple processes
                try
                {
                    lock (_lockObject)
                    {
                        // Use FileStream with FileShare.ReadWrite to allow multiple processes
                        var fileStream = new FileStream(
                            _logFilePath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite
                        );
                        
                        _logFileWriter = new StreamWriter(fileStream)
                        {
                            AutoFlush = true // Auto-save
                        };
                    }

                    return _logFilePath;
                }
                catch (UnauthorizedAccessException)
                {
                    // No permission to create file - continue without file logging
                    _logFilePath = string.Empty;
                    return string.Empty;
                }
                catch (IOException)
                {
                    // File in use or I/O error - continue without file logging
                    _logFilePath = string.Empty;
                    return string.Empty;
                }
                catch (Exception)
                {
                    // Any other error - continue without file logging
                    _logFilePath = string.Empty;
                    return string.Empty;
                }
            }
            catch (Exception)
            {
                // Last line of defense - never throw exception
                // If we got here, something very unexpected happened, but the app should continue
                _logFilePath = string.Empty;
                return string.Empty;
            }
        }

        /// <summary>
        /// Writes a message to the log file with timestamp and level
        /// </summary>
        /// <param name="message">Message to be logged</param>
        /// <param name="level">Log level (INFO, ERROR, WARNING, etc.)</param>
        public void WriteToLogFile(string message, string level = "INFO")
        {
            try
            {
                lock (_lockObject)
                {
                    if (_logFileWriter != null)
                    {
                        if (string.IsNullOrEmpty(level))
                        {
                            _logFileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
                        }
                        else
                        {
                            _logFileWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}");
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore file write errors
            }
        }

        /// <summary>
        /// Formats a log message with timestamp and level for console display
        /// </summary>
        /// <param name="message">Message to be formatted</param>
        /// <param name="level">Log level (INFO, ERROR, WARNING, etc.)</param>
        /// <returns>Formatted message for console</returns>
        public static string FormatConsoleMessage(string message, string level = "INFO")
        {
            // Get date format from LocalizationManager based on current language
            string dateFormat = LocalizationManager.GetDateFormat();
            string dateStr = DateTime.Now.ToString(dateFormat);
            string timeStr = DateTime.Now.ToString("HH:mm:ss");
            
            // If level is empty or null, don't add [level] prefix
            if (string.IsNullOrEmpty(level))
            {
                return $"[{dateStr}] [{timeStr}] {message}";
            }
            
            return $"[{dateStr}] [{timeStr}] [{level}] {message}";
        }

        /// <summary>
        /// Gets the full path to the logs folder
        /// </summary>
        /// <returns>Path to logs folder</returns>
        public static string GetLogsFolderPath()
        {
            return LogsFolderPath;
        }

        /// <summary>
        /// Releases logger resources (closes the log file)
        /// </summary>
        public void Dispose()
        {
            lock (_lockObject)
            {
                try
                {
                    _logFileWriter?.Close();
                    _logFileWriter?.Dispose();
                    _logFileWriter = null;
                }
                catch
                {
                    // Ignore errors when closing log
                }
            }
        }
    }
}