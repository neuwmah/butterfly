using System.ComponentModel;

namespace Butterfly.Models
{
    public class Server : INotifyPropertyChanged
    {
        private string name = string.Empty;
        private string url = string.Empty;
        private string rankingUrl = string.Empty;
        private string status = string.Empty;
        private int onlinePlayers = 0;
        private bool isSelected = false;

        public string Name
        {
            get => name;
            set
            {
                if (name != value)
                {
                    name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public string Url
        {
            get => url;
            set
            {
                if (url != value)
                {
                    url = value;
                    OnPropertyChanged(nameof(Url));
                }
            }
        }

        public string RankingUrl
        {
            get => rankingUrl;
            set
            {
                if (rankingUrl != value)
                {
                    rankingUrl = value;
                    OnPropertyChanged(nameof(RankingUrl));
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

        public int OnlinePlayers
        {
            get => onlinePlayers;
            set
            {
                if (onlinePlayers != value)
                {
                    onlinePlayers = value;
                    OnPropertyChanged(nameof(OnlinePlayers));
                }
            }
        }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected != value)
                {
                    isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
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
