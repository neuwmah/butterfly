using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Butterfly.Converters
{
    public class BotVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
                return Visibility.Collapsed;
            
            bool isServerSelected = values[0] is bool selected && selected;
            string status = values[1] as string ?? "";
            
            bool shouldShow = isServerSelected && (status == "Online" || status == "Offline");
            
            return shouldShow ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
