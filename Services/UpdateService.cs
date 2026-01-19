using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Butterfly.Services
{
    /// <summary>
    /// Service for automatic application verification and updates
    /// </summary>
    public class UpdateService
    {
        // Configuration URLs
        private const string VERSION_URL = "http://butterfly.beer/updates/version.txt";
        private const string UPDATE_URL = "http://butterfly.beer/updates/Butterfly.exe";
        
        /// <summary>
        /// Callback to request logs in the internal console
        /// </summary>
        public Action<string, string>? OnLogRequested { get; set; }
        
        // Update file name (hidden file starting with dot)
        private const string UPDATE_FILE_NAME = ".update.exe";
        
        /// <summary>
        /// Checks if a newer version is available
        /// </summary>
        /// <param name="currentVersion">Current application version</param>
        /// <returns>True if update is available, False otherwise</returns>
        public async Task<bool> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_CheckingForUpdates"), "REQUEST");
                
                // Configure HttpClientHandler with custom SSL validation
                // This allows ignoring expired/invalid SSL certificate errors
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                    AllowAutoRedirect = true // Allows redirection, but we'll keep the base URL in HTTP
                };
                
                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    
                    // Configure headers to ignore cache
                    httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true,
                        MustRevalidate = true
                    };
                    if (!httpClient.DefaultRequestHeaders.Pragma.Contains(new NameValueHeaderValue("no-cache")))
                    {
                        httpClient.DefaultRequestHeaders.Pragma.Add(new NameValueHeaderValue("no-cache"));
                    }
                    
                    // Fetch remote version with cache busting
                    string versionUrlWithCacheBust = $"{VERSION_URL}?t={DateTime.Now.Ticks}";
                    string remoteVersion = await httpClient.GetStringAsync(versionUrlWithCacheBust);
                    remoteVersion = remoteVersion.Trim();
                    
                    // Compare versions using Version class for semantic comparison
                    try
                    {
                        var currentVer = new Version(currentVersion);
                        var remoteVer = new Version(remoteVersion);
                        
                        if (remoteVer > currentVer)
                        {
                            OnLogRequested?.Invoke(LocalizationManager.GetString("Update_NewVersionAvailable", currentVersion, remoteVersion), "INFO");
                            return true;
                        }
                        else
                        {
                            OnLogRequested?.Invoke($"v{currentVersion}", "SUCCESS");
                            return false;
                        }
                    }
                    catch (Exception versionEx)
                    {
                        // If conversion to Version fails, use string comparison as fallback
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_VersionFormatError", versionEx.Message), "INFO");
                        if (remoteVersion != currentVersion)
                        {
                            OnLogRequested?.Invoke(LocalizationManager.GetString("Update_NewVersionAvailable", currentVersion, remoteVersion), "INFO");
                            return true;
                        }
                        else
                        {
                            OnLogRequested?.Invoke($"v{currentVersion}", "SUCCESS");
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_CheckFailed", ex.Message), "ERROR");
                
                // Silently ignore connection errors
                // We don't want to interrupt application usage if update check fails
            }
            
            return false;
        }
        
        /// <summary>
        /// Gets the real directory where the executable is located
        /// This handles both normal execution and Single File Publish scenarios
        /// </summary>
        private string GetExecutableDirectory()
        {
            try
            {
                // Use Process.GetCurrentProcess().MainModule.FileName to get the real executable path
                // This works correctly even with Single File Publish (unlike AppDomain.CurrentDomain.BaseDirectory)
                string executablePath = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                
                if (string.IsNullOrEmpty(executablePath))
                {
                    // Fallback to AppDomain if both methods fail (shouldn't happen)
                    return AppDomain.CurrentDomain.BaseDirectory;
                }
                else
                {
                    // Get directory from executable path (Path.GetDirectoryName can return null)
                    string? directory = Path.GetDirectoryName(executablePath);
                    return directory ?? AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            catch
            {
                // Fallback to AppDomain if exception occurs
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
        
        /// <summary>
        /// Downloads and installs the update
        /// </summary>
        /// <param name="progress">Optional progress reporter for download progress (0.0 to 100.0)</param>
        /// <returns>True if the process was started successfully</returns>
        public async Task<bool> DownloadAndInstallUpdateAsync(IProgress<double>? progress = null)
        {
            try
            {
                // Get the real executable directory (handles Single File Publish correctly)
                string baseDirectory = GetExecutableDirectory();
                string executableFileName = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? Path.Combine(baseDirectory, "Butterfly.exe");
                string executableName = Path.GetFileName(executableFileName);
                string updateFilePath = Path.Combine(baseDirectory, UPDATE_FILE_NAME);
                string batFilePath = Path.Combine(baseDirectory, ".update.bat");
                
                // Clean up old update files
                if (File.Exists(updateFilePath))
                {
                    try
                    {
                        File.Delete(updateFilePath);
                    }
                    catch (Exception)
                    {
                        // Ignore cleanup errors
                    }
                }
                
                if (File.Exists(batFilePath))
                {
                    try
                    {
                        File.Delete(batFilePath);
                    }
                    catch (Exception)
                    {
                        // Ignore cleanup errors
                    }
                }
                
                // Log download start - unified message
                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_Downloading"), "");
                
                // Ensure directory exists (should always exist, but just in case)
                if (!Directory.Exists(baseDirectory))
                {
                    OnLogRequested?.Invoke(LocalizationManager.GetString("Update_DirectoryNotExist", baseDirectory), "ERROR");
                    return false;
                }
                
                // Old update file should have been cleaned up at the start, but try again if it exists
                if (File.Exists(updateFilePath))
                {
                    try
                    {
                        File.Delete(updateFilePath);
                    }
                    catch (Exception)
                    {
                        // Ignore cleanup errors
                    }
                }
                
                // Download the new executable using streams for better memory efficiency
                // Configure HttpClientHandler with custom SSL validation
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                    AllowAutoRedirect = true // Allows redirection, but we'll keep the base URL in HTTP
                };
                
                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                    
                    try
                    {
                        // Use GetAsync with ResponseHeadersRead to start reading immediately
                        using (var response = await httpClient.GetAsync(UPDATE_URL, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            
                            // Get content length for progress calculation
                            long? contentLength = response.Content.Headers.ContentLength;
                            long totalBytes = contentLength ?? 0;
                            long downloadedBytes = 0;
                            const int bufferSize = 8192; // 8KB buffer
                            
                            // Read the response stream and write directly to file
                            using (var responseStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(updateFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                            {
                                var buffer = new byte[bufferSize];
                                int bytesRead;
                                
                                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, bufferSize)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    downloadedBytes += bytesRead;
                                    
                                    // Report progress if progress reporter is available and we know the total size
                                    if (progress != null && totalBytes > 0)
                                    {
                                        double percentComplete = (double)downloadedBytes / totalBytes * 100.0;
                                        progress.Report(percentComplete);
                                    }
                                }
                            }
                            
                            // Validate downloaded file has content
                            if (!File.Exists(updateFilePath))
                            {
                                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_FailedWriteFile"), "ERROR");
                                return false;
                            }
                            
                            FileInfo fileInfo = new FileInfo(updateFilePath);
                            if (fileInfo.Length == 0)
                            {
                                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_DownloadFailedEmpty"), "ERROR");
                                return false;
                            }
                            
                            // Verify file size if we knew the expected size
                            if (totalBytes > 0 && fileInfo.Length != totalBytes)
                            {
                                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_FileSizeMismatch", totalBytes, fileInfo.Length), "ERROR");
                                return false;
                            }
                            
                            // Report 100% progress
                            progress?.Report(100.0);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_PermissionDenied", ex.Message), "ERROR");
                        
                        // Check if the directory is in Program Files and suggest moving
                        if (baseDirectory.Contains("Program Files", StringComparison.OrdinalIgnoreCase))
                        {
                            OnLogRequested?.Invoke(LocalizationManager.GetString("Update_TipProgramFiles"), "INFO");
                        }
                        else
                        {
                            OnLogRequested?.Invoke(LocalizationManager.GetString("Update_EnsureWritePermissions"), "INFO");
                        }
                        
                        return false;
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_DirectoryNotFound", baseDirectory, ex.Message), "ERROR");
                        return false;
                    }
                    catch (IOException ex)
                    {
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_FileIOError", ex.Message), "ERROR");
                        return false;
                    }
                    catch (HttpRequestException ex)
                    {
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_NetworkError", ex.Message), "ERROR");
                        return false;
                    }
                    catch (TaskCanceledException ex)
                    {
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_DownloadTimeout", ex.Message), "ERROR");
                        return false;
                    }
                }
                
                // Download completed - application will restart
                // Message remains: "Downloading update, please wait..." until application closes
                
                // Get current process ID for waiting in batch script
                int currentProcessId = Process.GetCurrentProcess().Id;
                
                // Create .bat file for update (hidden file starting with dot)
                // Note: batFilePath was already declared at the start of the method
                string exePath = executableFileName; // Use full path to current executable
                string newExePath = updateFilePath;
                string finalExeName = executableName; // Will be "Butterfly.exe" typically - MUST be filename only, no path
                string newExeFileName = Path.GetFileName(newExePath); // Just the filename for ren command
                
                // Validate that finalExeName is just a filename (safety check)
                if (finalExeName.Contains(Path.DirectorySeparatorChar) || finalExeName.Contains(Path.AltDirectorySeparatorChar))
                {
                    OnLogRequested?.Invoke(LocalizationManager.GetString("Update_ErrorFinalExeName", finalExeName), "ERROR");
                    finalExeName = Path.GetFileName(finalExeName); // Fix it
                }
                
                // Create .bat script that waits for the process to exit before replacing
                // Simple and direct version
                string batContent = $@"@echo off
setlocal
set ""EXE_DIR={baseDirectory}""
set ""NEW_EXE_NAME={newExeFileName}""
set ""FINAL_EXE_NAME={finalExeName}""
set ""PID={currentProcessId}""

cd /d ""%EXE_DIR%""

:WAIT_CLOSE
tasklist /FI ""PID eq %PID%"" 2>nul | find /I ""%PID%"" >nul
if not errorlevel 1 (
    ping 127.0.0.1 -n 1 -w 500 >nul
    goto WAIT_CLOSE
)

if exist ""%FINAL_EXE_NAME%"" del /f /q ""%FINAL_EXE_NAME%""
if exist ""%NEW_EXE_NAME%"" ren ""%NEW_EXE_NAME%"" ""%FINAL_EXE_NAME%""

powershell -Command ""$p = Start-Process '%FINAL_EXE_NAME%' -PassThru; while($p.MainWindowHandle -eq 0) {{ Start-Sleep -Milliseconds 100; $p.Refresh() }}; (New-Object -ComObject WScript.Shell).AppActivate($p.Id)""

del /f /q ""%~f0""
endlocal
";
                
                try
                {
                    File.WriteAllText(batFilePath, batContent);
                    
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{batFilePath}\"",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    
                    // Close the current application
                    Application.Current.Shutdown();
                    
                    return true;
                }
                catch (Exception ex)
                {
                    OnLogRequested?.Invoke(LocalizationManager.GetString("Update_ErrorGeneric", ex.Message), "ERROR");
                    return false;
                }
            }
            catch (Exception ex)
            {
                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_InstallationFailed", ex.Message), "ERROR");
                
                // Keep MessageBox for unexpected critical errors
                MessageBox.Show(
                    LocalizationManager.GetString("Msg_UpdateError", ex.Message),
                    LocalizationManager.GetString("Msg_UpdateErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return false;
            }
        }
    }
}
