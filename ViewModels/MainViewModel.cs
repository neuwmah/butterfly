using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Butterfly.Models;

namespace Butterfly.ViewModels
{
    /// <summary>
    /// Main ViewModel for MainWindow
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private bool _isUsernameVisible = false;
        private bool _isPasswordVisible = false;
        private bool _isServerSelected = false;
        private Language _selectedLanguage;

        public ObservableCollection<Account> Accounts { get; } = new ObservableCollection<Account>();
        public ObservableCollection<Server> Servers { get; } = new ObservableCollection<Server>();
        public ObservableCollection<Language> Languages { get; }

        public MainViewModel()
        {
            Languages = new ObservableCollection<Language>
            {
                new Language("BR", "BR", "🇧🇷", "BR", "/Resources/Flags/br.png"),
                new Language("KR", "KR", "🇰🇷", "KR", "/Resources/Flags/kr.png"),
                new Language("JP", "JP", "🇯🇵", "JP", "/Resources/Flags/jp.png"),
                new Language("FR", "FR", "🇫🇷", "FR", "/Resources/Flags/fr.png"),
                new Language("US", "US", "🇺🇸", "US", "/Resources/Flags/us.png")
            };
            
            // Load saved language preference or default to US
            string savedLanguageCode = Butterfly.Services.LocalizationManager.Instance.LoadLanguagePreference();
            // Normalize PTBR to BR for matching (since Language objects use "BR" as Code)
            if (savedLanguageCode == "PTBR")
            {
                savedLanguageCode = "BR";
            }
            // Find matching language or default to US
            _selectedLanguage = Languages.FirstOrDefault(l => l.Code == savedLanguageCode) 
                ?? Languages.FirstOrDefault(l => l.Code == "US") 
                ?? Languages[Languages.Count - 1];
        }

        public Language SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (_selectedLanguage != value)
                {
                    _selectedLanguage = value;
                    OnPropertyChanged(nameof(SelectedLanguage));
                    
                    // Switch language when selected language changes
                    if (value != null)
                    {
                        Butterfly.Services.LocalizationManager.Instance.SwitchLanguage(value.Code);
                        // Save language preference after switching
                        Butterfly.Services.LocalizationManager.Instance.SaveLanguagePreference(value.Code);
                    }
                }
            }
        }

        public bool IsUsernameVisible
        {
            get => _isUsernameVisible;
            set
            {
                if (_isUsernameVisible != value)
                {
                    _isUsernameVisible = value;
                    OnPropertyChanged(nameof(IsUsernameVisible));
                }
            }
        }

        public bool IsPasswordVisible
        {
            get => _isPasswordVisible;
            set
            {
                if (_isPasswordVisible != value)
                {
                    _isPasswordVisible = value;
                    OnPropertyChanged(nameof(IsPasswordVisible));
                }
            }
        }

        public bool IsServerSelected
        {
            get => _isServerSelected;
            set
            {
                if (_isServerSelected != value)
                {
                    _isServerSelected = value;
                    OnPropertyChanged(nameof(IsServerSelected));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
