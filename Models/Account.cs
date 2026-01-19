using System.ComponentModel;

namespace Butterfly.Models
{
    /// <summary>
    /// User account model
    /// </summary>
    public class Account : INotifyPropertyChanged
    {
        private string status = string.Empty;
        private bool isEditing = false;
        private bool isBotActive = false;
        private bool isReconnecting = false;
        private string username = string.Empty;
        private string password = string.Empty;
        private string character = string.Empty;
        private int gameProcessId = 0;
        private string level = string.Empty;
        private string experience = string.Empty;

        public string Username 
        { 
            get => username;
            set
            {
                if (username != value)
                {
                    username = value;
                    OnPropertyChanged(nameof(Username));
                }
            }
        }

        public string Password 
        { 
            get => password;
            set
            {
                if (password != value)
                {
                    password = value;
                    OnPropertyChanged(nameof(Password));
                }
            }
        }

        public string Character 
        { 
            get => character;
            set
            {
                if (character != value)
                {
                    character = value;
                    OnPropertyChanged(nameof(Character));
                }
            }
        }

        public string Status
        {
            get => status;
            set
            {
                if (status != value)
                {
                    status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        public bool IsEditing
        {
            get => isEditing;
            set
            {
                if (isEditing != value)
                {
                    isEditing = value;
                    OnPropertyChanged(nameof(IsEditing));
                }
            }
        }

        public bool IsBotActive
        {
            get => isBotActive;
            set
            {
                if (isBotActive != value)
                {
                    isBotActive = value;
                    OnPropertyChanged(nameof(IsBotActive));
                }
            }
        }

        public bool IsReconnecting
        {
            get => isReconnecting;
            set
            {
                if (isReconnecting != value)
                {
                    isReconnecting = value;
                    OnPropertyChanged(nameof(IsReconnecting));
                }
            }
        }

        public int GameProcessId
        {
            get => gameProcessId;
            set
            {
                if (gameProcessId != value)
                {
                    gameProcessId = value;
                    OnPropertyChanged(nameof(GameProcessId));
                }
            }
        }

        public string Level
        {
            get => level;
            set
            {
                if (level != value)
                {
                    level = value;
                    OnPropertyChanged(nameof(Level));
                }
            }
        }

        public string Experience
        {
            get => experience;
            set
            {
                if (experience != value)
                {
                    experience = value;
                    OnPropertyChanged(nameof(Experience));
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
