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
                if (targetType == typeof(bool))
                {
                    return false;
                }
                return DependencyProperty.UnsetValue;
            }
            catch
            {
                return DependencyProperty.UnsetValue;
            }
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values == null || values.Length < 2)
                {
                    return Visibility.Collapsed;
                }

                string? paramStr = parameter?.ToString();
                
                if (paramStr == "MultiVisibility")
                {
                    if (values[0] is bool isVisible && values[1] is bool isEditing)
                    {
                        return (!isVisible && !isEditing) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                else if (paramStr == "MultiVisibilityAnd")
                {
                    if (values[0] is bool value1 && values[1] is bool value2)
                    {
                        return (!value1 && !value2) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                
                return Visibility.Collapsed;
            }
            catch
            {
                return Visibility.Collapsed;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
