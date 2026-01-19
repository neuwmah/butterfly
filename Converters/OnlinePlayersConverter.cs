using System;
using System.Globalization;
using System.Windows.Data;

namespace Butterfly.Converters
{
    public class OnlinePlayersConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length == 2 && values[0] is int onlinePlayers && values[1] is string status)
            {
                // Return empty string if value is 0, null or not available
                if (onlinePlayers == 0 || string.IsNullOrEmpty(status) || status == "-")
                {
                    return string.Empty;
                }
                
                // Return full format "Online: X"
                return $"Online: {onlinePlayers}";
            }
            
            // Return empty string if there are no valid values
            return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
