using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AdbExplorer.Converters
{
    public class AccessibilityColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isAccessible)
            {
                return isAccessible ? Brushes.Black : Brushes.Gray;
            }
            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
