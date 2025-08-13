using System;
using System.Reflection;
using System.Windows;

namespace AdbExplorer
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
            LoadVersionInfo();
        }

        private void LoadVersionInfo()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                VersionText.Text = "Version 1.0.0";
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}