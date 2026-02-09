using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AdbExplorer.Models
{
    public class AppInfo : INotifyPropertyChanged
    {
        private string label = "";
        private ImageSource? icon;

        public string PackageName { get; set; } = "";

        public string Label
        {
            get => label;
            set { label = value; OnPropertyChanged(); }
        }

        public string ApkPath { get; set; } = "";
        public bool IsSystemApp { get; set; } = false;

        public ImageSource? Icon
        {
            get => icon;
            set { icon = value; OnPropertyChanged(); }
        }

        public override string ToString() => string.IsNullOrEmpty(Label) ? PackageName : Label;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
