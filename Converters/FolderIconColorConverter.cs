using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AdbExplorer.Models;

namespace AdbExplorer.Converters
{
    public class FolderIconColorConverter : IValueConverter
    {
        // Windows 11 folder yellow color
        private static readonly SolidColorBrush FolderYellow = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x17));
        private static readonly SolidColorBrush DefaultColor = Brushes.Black;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FileItem file && file.IsDirectory)
            {
                return FolderYellow;
            }
            return DefaultColor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
