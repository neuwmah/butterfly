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
                if (onlinePlayers == 0 || string.IsNullOrEmpty(status) || status == "-")
                {
                    return string.Empty;
                }
                
                return onlinePlayers.ToString();
            }
            
            return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
