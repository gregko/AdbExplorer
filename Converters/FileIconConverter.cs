using System;
using System.Globalization;
using System.Windows.Data;
using AdbExplorer.Models;

namespace AdbExplorer.Converters
{
    public class FileIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FileItem file)
            {
                if (file.IsDirectory)
                    return "📁";
                
                var ext = System.IO.Path.GetExtension(file.Name).ToLower();
                return ext switch
                {
                    ".txt" or ".log" or ".conf" or ".cfg" => "📄",
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" => "🖼️",
                    ".mp4" or ".avi" or ".mkv" or ".mov" => "🎬",
                    ".mp3" or ".wav" or ".ogg" or ".m4a" => "🎵",
                    ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "📦",
                    ".apk" => "📱",
                    ".pdf" => "📕",
                    ".doc" or ".docx" => "📘",
                    ".xls" or ".xlsx" => "📊",
                    ".xml" or ".json" => "🔧",
                    ".html" or ".htm" => "🌐",
                    ".exe" or ".dll" => "⚙️",
                    _ => "📄"
                };
            }
            return "📄";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
