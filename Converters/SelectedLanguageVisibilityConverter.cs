using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Butterfly.Models;

namespace Butterfly.Converters
{
    public class SelectedLanguageVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length != 2)
                return Visibility.Visible;

            var currentLanguage = values[0] as Language;
            var selectedLanguage = values[1] as Language;

            if (currentLanguage == null || selectedLanguage == null)
                return Visibility.Visible;

            // Hide if current language is the same as selected language
            if (currentLanguage.Code == selectedLanguage.Code)
                return Visibility.Collapsed;

            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
