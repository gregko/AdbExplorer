using System;
using System.Linq;
using System.Windows;
using AdbExplorer.Services;

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

            var cmdArgs = Environment.GetCommandLineArgs();

            if (cmdArgs.Contains("-apps", StringComparer.OrdinalIgnoreCase))
            {
                HandleAppsMode();
            }
            else if (cmdArgs.Contains("-launch", StringComparer.OrdinalIgnoreCase))
            {
                HandleLaunchMode(cmdArgs);
            }
            else
            {
                // Normal mode
                var mainWindow = new MainWindow();
                mainWindow.Show();
            }
        }

        private void HandleAppsMode()
        {
            if (!AppService.IsWsaInstalled())
            {
                MessageBox.Show("Windows Subsystem for Android is not installed.",
                    "ADB Explorer - App Drawer", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            var drawer = new AppDrawerWindow();
            drawer.Show();
        }

        private void HandleLaunchMode(string[] cmdArgs)
        {
            int launchIdx = Array.FindIndex(cmdArgs,
                a => a.Equals("-launch", StringComparison.OrdinalIgnoreCase));

            if (launchIdx + 1 >= cmdArgs.Length)
            {
                MessageBox.Show("Usage: AdbExplorer -launch <package>",
                    "ADB Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            string packageName = cmdArgs[launchIdx + 1];
            var appService = new AppService();

            try
            {
                appService.LaunchApp(packageName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch {packageName}:\n{ex.Message}",
                    "ADB Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown();
        }
    }
}
