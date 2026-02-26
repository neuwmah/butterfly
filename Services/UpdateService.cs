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
    public class UpdateService
    {
        private const string VERSION_URL = "http://butterfly.beer/updates/version.txt";
        private const string UPDATE_URL = "http://butterfly.beer/updates/Butterfly.exe";
        
        public Action<string, string>? OnLogRequested { get; set; }
        
        private const string UPDATE_FILE_NAME = ".update.exe";
        
        public async Task<bool> CheckForUpdateAsync(string currentVersion)
        {
            try
            {
                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_CheckingForUpdates"), "REQUEST");
                
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                    AllowAutoRedirect = true
                };
                
                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    
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
                    
                    string versionUrlWithCacheBust = $"{VERSION_URL}?t={DateTime.Now.Ticks}";
                    string remoteVersion = await httpClient.GetStringAsync(versionUrlWithCacheBust);
                    remoteVersion = remoteVersion.Trim();
                    
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
            }
            
            return false;
        }
        
        private string GetExecutableDirectory()
        {
            try
            {
                string executablePath = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
                
                if (string.IsNullOrEmpty(executablePath))
                {
                    return AppDomain.CurrentDomain.BaseDirectory;
                }
                else
                {
                    string? directory = Path.GetDirectoryName(executablePath);
                    return directory ?? AppDomain.CurrentDomain.BaseDirectory;
                }
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
        
        public async Task<bool> DownloadAndInstallUpdateAsync(IProgress<double>? progress = null)
        {
            try
            {
                string baseDirectory = GetExecutableDirectory();
                string executableFileName = Process.GetCurrentProcess().MainModule?.FileName 
                    ?? Path.Combine(baseDirectory, "Butterfly.exe");
                string executableName = Path.GetFileName(executableFileName);
                string updateFilePath = Path.Combine(baseDirectory, UPDATE_FILE_NAME);
                string batFilePath = Path.Combine(baseDirectory, ".update.bat");
                
                if (File.Exists(updateFilePath))
                {
                    try
                    {
                        File.Delete(updateFilePath);
                    }
                    catch (Exception)
                    {
                        // ignore cleanup errors
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
                        // ignore cleanup errors
                    }
                }
                
                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_Downloading"), "");
                
                if (!Directory.Exists(baseDirectory))
                {
                    OnLogRequested?.Invoke(LocalizationManager.GetString("Update_DirectoryNotExist", baseDirectory), "ERROR");
                    return false;
                }
                
                if (File.Exists(updateFilePath))
                {
                    try
                    {
                        File.Delete(updateFilePath);
                    }
                    catch (Exception)
                    {
                        // ignore cleanup errors
                    }
                }
                
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                    AllowAutoRedirect = true
                };
                
                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                    
                    try
                    {
                        using (var response = await httpClient.GetAsync(UPDATE_URL, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            
                            long? contentLength = response.Content.Headers.ContentLength;
                            long totalBytes = contentLength ?? 0;
                            long downloadedBytes = 0;
                            const int bufferSize = 8192;
                            
                            using (var responseStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = new FileStream(updateFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                            {
                                var buffer = new byte[bufferSize];
                                int bytesRead;
                                
                                while ((bytesRead = await responseStream.ReadAsync(buffer, 0, bufferSize)) > 0)
                                {
                                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                                    downloadedBytes += bytesRead;
                                    
                                    if (progress != null && totalBytes > 0)
                                    {
                                        double percentComplete = (double)downloadedBytes / totalBytes * 100.0;
                                        progress.Report(percentComplete);
                                    }
                                }
                            }
                            
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
                            
                            if (totalBytes > 0 && fileInfo.Length != totalBytes)
                            {
                                OnLogRequested?.Invoke(LocalizationManager.GetString("Update_FileSizeMismatch", totalBytes, fileInfo.Length), "ERROR");
                                return false;
                            }
                            
                            progress?.Report(100.0);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        OnLogRequested?.Invoke(LocalizationManager.GetString("Update_PermissionDenied", ex.Message), "ERROR");
                        
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
                
                int currentProcessId = Process.GetCurrentProcess().Id;
                
                string exePath = executableFileName;
                string newExePath = updateFilePath;
                string finalExeName = executableName;
                string newExeFileName = Path.GetFileName(newExePath);
                
                if (finalExeName.Contains(Path.DirectorySeparatorChar) || finalExeName.Contains(Path.AltDirectorySeparatorChar))
                {
                    OnLogRequested?.Invoke(LocalizationManager.GetString("Update_ErrorFinalExeName", finalExeName), "ERROR");
                    finalExeName = Path.GetFileName(finalExeName);
                }
                
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
