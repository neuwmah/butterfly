using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;

namespace Butterfly.Services
{
    /// <summary>
    /// Singleton manager for application localization
    /// Manages ResourceDictionary switching for XAML and provides string access for C# code
    /// </summary>
    public class LocalizationManager
    {
        private static LocalizationManager? _instance;
        private static readonly object _lock = new object();
        
        private ResourceDictionary? _currentLanguageDictionary;
        
        // Mapping between language codes and culture codes
        private readonly Dictionary<string, string> _languageCodeMap = new Dictionary<string, string>
        {
            { "US", "en-US" },
            { "PTBR", "pt-BR" },
            { "BR", "pt-BR" }, // Alternative code
            { "KR", "ko-KR" },
            { "JP", "ja-JP" },
            { "FR", "fr-FR" }
        };
        
        private LocalizationManager()
        {
            // Load saved language preference or default to US
            string savedLanguage = LoadLanguagePreference();
            SwitchLanguage(savedLanguage);
        }
        
        public static LocalizationManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new LocalizationManager();
                        }
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Switches the application language at runtime
        /// </summary>
        /// <param name="languageCode">Language code (US, PTBR, KR, JP, FR)</param>
        public void SwitchLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                languageCode = "US"; // Default to English
            }
            
            // Normalize code (handle PTBR/BR)
            if (languageCode == "PTBR" || languageCode == "BR")
            {
                languageCode = "PTBR";
            }
            
            if (!_languageCodeMap.TryGetValue(languageCode, out var cultureCode))
            {
                cultureCode = "en-US"; // Fallback to English
            }
            
            // Store language code even if Application.Current is not available yet
            CurrentLanguageCode = languageCode;
            
            // Check if Application.Current is available before trying to switch resources
            if (Application.Current == null)
            {
                // Application not initialized yet - just store the language code
                // The language will be applied when Application.Current becomes available
                return;
            }
            
            // Construct resource dictionary path
            var resourcePath = $"Resources/Languages/Strings.{cultureCode}.xaml";
            
            try
            {
                // Remove current language dictionary if exists
                if (_currentLanguageDictionary != null)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(_currentLanguageDictionary);
                    _currentLanguageDictionary = null;
                }
                
                // Load new resource dictionary
                var newDictionary = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute)
                };
                
                // Add to application resources (must be at index 0 to override defaults)
                Application.Current.Resources.MergedDictionaries.Insert(0, newDictionary);
                _currentLanguageDictionary = newDictionary;
            }
            catch (Exception ex)
            {
                // If resource file doesn't exist, log and fallback to English
                System.Diagnostics.Debug.WriteLine($"Failed to load language resource '{resourcePath}': {ex.Message}");
                
                // Try to load en-US as fallback
                if (cultureCode != "en-US")
                {
                    SwitchLanguage("US");
                }
            }
        }
        
        /// <summary>
        /// Gets a localized string by key
        /// </summary>
        /// <param name="key">Resource key</param>
        /// <returns>Localized string or key if not found</returns>
        public static string GetString(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }
            
            try
            {
                var resource = Application.Current.FindResource(key);
                return resource?.ToString() ?? key;
            }
            catch
            {
                // Resource not found, return key
                return key;
            }
        }
        
        /// <summary>
        /// Gets a localized string with formatted arguments
        /// </summary>
        /// <param name="key">Resource key</param>
        /// <param name="args">Format arguments</param>
        /// <returns>Formatted localized string</returns>
        public static string GetString(string key, params object[] args)
        {
            var format = GetString(key);
            try
            {
                return string.Format(format, args);
            }
            catch
            {
                return format;
            }
        }
        
        /// <summary>
        /// Gets the current language code
        /// </summary>
        public string CurrentLanguageCode { get; private set; } = "US";
        
        /// <summary>
        /// Gets the date format string for the current language
        /// </summary>
        /// <returns>Date format string (e.g., "MM/dd/yyyy", "dd/MM/yyyy", "yyyy/MM/dd")</returns>
        public static string GetDateFormat()
        {
            string dateFormat = GetString("Config_DateFormat");
            // Fallback to default if not found or if key was returned unchanged
            if (string.IsNullOrEmpty(dateFormat) || dateFormat == "Config_DateFormat")
            {
                dateFormat = "MM/dd/yyyy"; // Default to US format
            }
            return dateFormat;
        }

        /// <summary>
        /// Saves the language preference to a file in the .Butterfly folder
        /// </summary>
        /// <param name="languageCode">Language code to save (e.g., "US", "BR", "KR", "JP", "FR")</param>
        public void SaveLanguagePreference(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                return;
            }

            // Normalize BR to PTBR for consistency
            if (languageCode == "BR")
            {
                languageCode = "PTBR";
            }

            try
            {
                // Get the .Butterfly folder path
                string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                
                // Create folder if it doesn't exist
                if (!Directory.Exists(butterflyFolder))
                {
                    var directoryInfo = Directory.CreateDirectory(butterflyFolder);
                    // Make the folder hidden
                    try
                    {
                        if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            File.SetAttributes(butterflyFolder, directoryInfo.Attributes | FileAttributes.Hidden);
                        }
                    }
                    catch
                    {
                        // Ignore errors when setting hidden attribute
                    }
                }

                // Save language code to file
                string languageFilePath = Path.Combine(butterflyFolder, "language.dat");
                File.WriteAllText(languageFilePath, languageCode);
            }
            catch (UnauthorizedAccessException)
            {
                // No permission to write - silently ignore
                System.Diagnostics.Debug.WriteLine("Failed to save language preference: UnauthorizedAccessException");
            }
            catch (IOException ex)
            {
                // I/O error - silently ignore
                System.Diagnostics.Debug.WriteLine($"Failed to save language preference: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Any other error - silently ignore
                System.Diagnostics.Debug.WriteLine($"Failed to save language preference: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the language preference from a file in the .Butterfly folder
        /// </summary>
        /// <returns>Language code (e.g., "US", "BR", "KR", "JP", "FR") or "US" if file doesn't exist or error occurs</returns>
        public string LoadLanguagePreference()
        {
            try
            {
                // Get the .Butterfly folder path
                string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                string languageFilePath = Path.Combine(butterflyFolder, "language.dat");

                // Check if file exists
                if (File.Exists(languageFilePath))
                {
                    // Read language code from file
                    string languageCode = File.ReadAllText(languageFilePath).Trim();
                    
                    // Validate language code
                    if (!string.IsNullOrEmpty(languageCode))
                    {
                        // Normalize BR to PTBR
                        if (languageCode == "BR")
                        {
                            languageCode = "PTBR";
                        }
                        
                        // Verify it's a valid language code
                        if (_languageCodeMap.ContainsKey(languageCode))
                        {
                            return languageCode;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // No permission to read - silently ignore and return default
                System.Diagnostics.Debug.WriteLine("Failed to load language preference: UnauthorizedAccessException");
            }
            catch (IOException ex)
            {
                // I/O error - silently ignore and return default
                System.Diagnostics.Debug.WriteLine($"Failed to load language preference: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Any other error - silently ignore and return default
                System.Diagnostics.Debug.WriteLine($"Failed to load language preference: {ex.Message}");
            }

            // Return default language if file doesn't exist or error occurs
            return "US";
        }
    }
}
