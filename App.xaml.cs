using System;
using System.Windows;

namespace AdbExplorer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                MessageBox.Show($"Unhandled exception: {args.ExceptionObject}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Create the first window
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }
    }
}