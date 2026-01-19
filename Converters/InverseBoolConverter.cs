using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Butterfly.Converters
{
    public class InverseBoolConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is bool boolValue)
                {
                    if (parameter?.ToString() == "Opacity")
                    {
                        return boolValue ? 0.0 : 1.0;
                    }
                    return !boolValue;
                }
                // If not bool, return default value based on targetType
                if (targetType == typeof(bool))
                {
                    return false;
                }
                if (targetType == typeof(double))
                {
                    return 1.0;
                }
                return DependencyProperty.UnsetValue;
            }
            catch
            {
                // In case of any error, return safe default value
                return DependencyProperty.UnsetValue;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is bool boolValue)
                {
                    return !boolValue;
                }
                // If not bool, return default value
                if (targetType == typeof(bool))
                {
                    return false;
                }
                return DependencyProperty.UnsetValue;
            }
            catch
            {
                // In case of any error, return safe default value
                return DependencyProperty.UnsetValue;
            }
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                // Validate that values is not null and has enough elements
                if (values == null || values.Length < 2)
                {
                    return Visibility.Collapsed;
                }

                string? paramStr = parameter?.ToString();
                
                if (paramStr == "MultiVisibility")
                {
                    // Validate types before casting
                    if (values[0] is bool isVisible && values[1] is bool isEditing)
                    {
                        return (!isVisible && !isEditing) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                else if (paramStr == "MultiVisibilityAnd")
                {
                    // Validate types before casting
                    if (values[0] is bool value1 && values[1] is bool value2)
                    {
                        return (!value1 && !value2) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                
                // Safe default value if it doesn't match any case
                return Visibility.Collapsed;
            }
            catch
            {
                // In case of any error, return safe default value
                return Visibility.Collapsed;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
