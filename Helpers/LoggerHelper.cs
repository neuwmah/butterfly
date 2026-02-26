using System;
using System.IO;
using Butterfly.Services;

namespace Butterfly.Helpers
{
    public class LoggerHelper : IDisposable
    {
        private StreamWriter? _logFileWriter;
        private string _logFilePath = string.Empty;
        private readonly object _lockObject = new object();

        public static string LogsFolderPath => Path.Combine(App.RealExecutablePath, ".Butterfly", "logs");

        public string InitializeLogging()
        {
            try
            {
                if (!Directory.Exists(LogsFolderPath))
                {
                    try
                    {
                        var directoryInfo = Directory.CreateDirectory(LogsFolderPath);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return string.Empty;
                    }
                    catch (IOException)
                    {
                        return string.Empty;
                    }
                    catch (Exception)
                    {
                        return string.Empty;
                    }
                }

                try
                {
                    string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                    if (Directory.Exists(butterflyFolder))
                    {
                        var butterflyDirInfo = new DirectoryInfo(butterflyFolder);
                        if ((butterflyDirInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
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
                    // no permission to change attributes - silently ignore
                }
                catch (IOException)
                {
                    // I/O error when changing attributes - silently ignore
                }
                catch (Exception)
                {
                    // any other error when changing attributes - silently ignore
                }

                int processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(LogsFolderPath, $"butterfly_{processId}_{timestamp}.log");

                try
                {
                    lock (_lockObject)
                    {
                        var fileStream = new FileStream(
                            _logFilePath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite
                        );
                        
                        _logFileWriter = new StreamWriter(fileStream)
                        {
                            AutoFlush = true
                        };
                    }

                    return _logFilePath;
                }
                catch (UnauthorizedAccessException)
                {
                    _logFilePath = string.Empty;
                    return string.Empty;
                }
                catch (IOException)
                {
                    _logFilePath = string.Empty;
                    return string.Empty;
                }
                catch (Exception)
                {
                    _logFilePath = string.Empty;
                    return string.Empty;
                }
            }
            catch (Exception)
            {
                _logFilePath = string.Empty;
                return string.Empty;
            }
        }

        public void WriteToLogFile(string message, string level = "INFO")
        {
            try
            {
                lock (_lockObject)
                {
                    if (_logFileWriter != null)
                    {
                        _logFileWriter.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
                    }
                }
            }
            catch
            {
                // silently ignore file write errors
            }
        }

        public static string FormatConsoleMessage(string message, string level = "INFO")
        {
            string dateFormat = LocalizationManager.GetDateFormat();
            string dateStr = DateTime.Now.ToString(dateFormat);
            string timeStr = DateTime.Now.ToString("HH:mm:ss");
            
            return $"{dateStr} {timeStr} {message}";
        }

        public static string GetLogsFolderPath()
        {
            return LogsFolderPath;
        }

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
                    // ignore errors when closing log
                }
            }
        }
    }
}