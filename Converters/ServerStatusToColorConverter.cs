using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Butterfly.Converters
{
    public class ServerStatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "Online" => new SolidColorBrush(Color.FromRgb(0, 255, 0)),
                    "Offline" => new SolidColorBrush(Color.FromRgb(255, 0, 0)),
                    "Checking..." => new SolidColorBrush(Color.FromRgb(255, 255, 0)),
                    "Paused" => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                    "Idle" => new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                    _ => new SolidColorBrush(Color.FromRgb(128, 128, 128))
                };
            }
            return new SolidColorBrush(Color.FromRgb(128, 128, 128));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
