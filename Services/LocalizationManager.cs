using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;

namespace Butterfly.Services
{
    public class LocalizationManager
    {
        private static LocalizationManager? _instance;
        private static readonly object _lock = new object();
        
        private ResourceDictionary? _currentLanguageDictionary;
        
        private readonly Dictionary<string, string> _languageCodeMap = new Dictionary<string, string>
        {
            { "US", "en-US" },
            { "PTBR", "pt-BR" },
            { "BR", "pt-BR" },
            { "KR", "ko-KR" },
            { "JP", "ja-JP" },
            { "FR", "fr-FR" }
        };
        
        private LocalizationManager()
        {
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
        
        public void SwitchLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                languageCode = "US";
            }
            
            if (languageCode == "PTBR" || languageCode == "BR")
            {
                languageCode = "PTBR";
            }
            
            if (!_languageCodeMap.TryGetValue(languageCode, out var cultureCode))
            {
                cultureCode = "en-US";
            }
            
            CurrentLanguageCode = languageCode;
            
            if (Application.Current == null)
            {
                return;
            }
            
            var resourcePath = $"Resources/Languages/Strings.{cultureCode}.xaml";
            
            try
            {
                if (_currentLanguageDictionary != null)
                {
                    Application.Current.Resources.MergedDictionaries.Remove(_currentLanguageDictionary);
                    _currentLanguageDictionary = null;
                }
                
                var newDictionary = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{resourcePath}", UriKind.Absolute)
                };
                
                Application.Current.Resources.MergedDictionaries.Insert(0, newDictionary);
                _currentLanguageDictionary = newDictionary;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load language resource '{resourcePath}': {ex.Message}");
                
                if (cultureCode != "en-US")
                {
                    SwitchLanguage("US");
                }
            }
        }
        
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
                return key;
            }
        }
        
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
        
        public string CurrentLanguageCode { get; private set; } = "US";
        
        public static string GetDateFormat()
        {
            string dateFormat = GetString("Config_DateFormat");
            if (string.IsNullOrEmpty(dateFormat) || dateFormat == "Config_DateFormat")
            {
                dateFormat = "MM/dd/yyyy";
            }
            return dateFormat;
        }

        public void SaveLanguagePreference(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                return;
            }

            if (languageCode == "BR")
            {
                languageCode = "PTBR";
            }

            try
            {
                string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                
                if (!Directory.Exists(butterflyFolder))
                {
                    var directoryInfo = Directory.CreateDirectory(butterflyFolder);
                    try
                    {
                        if ((directoryInfo.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                        {
                            File.SetAttributes(butterflyFolder, directoryInfo.Attributes | FileAttributes.Hidden);
                        }
                    }
                    catch
                    {
                        // ignore errors when setting hidden attribute
                    }
                }

                string languageFilePath = Path.Combine(butterflyFolder, "language.dat");
                File.WriteAllText(languageFilePath, languageCode);
            }
            catch (UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Failed to save language preference: UnauthorizedAccessException");
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save language preference: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save language preference: {ex.Message}");
            }
        }

        public string LoadLanguagePreference()
        {
            try
            {
                string butterflyFolder = Path.Combine(App.RealExecutablePath, ".Butterfly");
                string languageFilePath = Path.Combine(butterflyFolder, "language.dat");

                if (File.Exists(languageFilePath))
                {
                    string languageCode = File.ReadAllText(languageFilePath).Trim();
                    
                    if (!string.IsNullOrEmpty(languageCode))
                    {
                        if (languageCode == "BR")
                        {
                            languageCode = "PTBR";
                        }
                        
                        if (_languageCodeMap.ContainsKey(languageCode))
                        {
                            return languageCode;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Failed to load language preference: UnauthorizedAccessException");
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load language preference: {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load language preference: {ex.Message}");
            }

            return "US";
        }
    }
}
