using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AdbExplorer.Helpers;
using AdbExplorer.Models;
using AdbExplorer.Services;

namespace AdbExplorer
{
    public partial class AppDrawerWindow : Window
    {
        private readonly AppService appService;
        private readonly Settings settings;
        private ObservableCollection<AppInfo> apps;
        private ICollectionView appsView;

        // Long-press handling
        private DispatcherTimer? longPressTimer;
        private AppInfo? longPressApp;
        private bool isLongPress;

        // Drag handling
        private Point dragStartPoint;
        private bool isDragReady;

        public AppDrawerWindow()
        {
            InitializeComponent();

            this.appService = new AppService();
            this.settings = Settings.Load();

            apps = new ObservableCollection<AppInfo>();
            appsView = CollectionViewSource.GetDefaultView(apps);
            appsView.Filter = FilterApp;
            AppGrid.ItemsSource = appsView;

            // Restore window size, position, and checkbox state
            Width = settings.AppDrawerWidth;
            Height = settings.AppDrawerHeight;
            ShowSystemAppsCheckBox.IsChecked = settings.AppDrawerShowSystemApps;

            if (!double.IsNaN(settings.AppDrawerLeft) && !double.IsNaN(settings.AppDrawerTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = settings.AppDrawerLeft;
                Top = settings.AppDrawerTop;
            }

            this.Loaded += AppDrawerWindow_Loaded;
            this.Closing += AppDrawerWindow_Closing;
        }

        private void AppDrawerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadApps();
        }

        private void AppDrawerWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Save window size, position, and checkbox state
            if (WindowState == WindowState.Normal)
            {
                settings.AppDrawerWidth = (int)Width;
                settings.AppDrawerHeight = (int)Height;
                settings.AppDrawerLeft = Left;
                settings.AppDrawerTop = Top;
            }
            settings.AppDrawerShowSystemApps = ShowSystemAppsCheckBox.IsChecked == true;
            settings.Save();

            CleanupDragTempFiles();
        }

        private void LoadApps()
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            StatusText.Text = "Loading apps...";
            apps.Clear();

            try
            {
                bool includeSystem = ShowSystemAppsCheckBox.IsChecked == true;
                var appList = appService.GetInstalledApps(includeSystem);

                foreach (var app in appList)
                    apps.Add(app);

                LoadingOverlay.Visibility = Visibility.Collapsed;
                UpdateStatusText();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void UpdateStatusText()
        {
            int visible = appsView.Cast<object>().Count();
            StatusText.Text = visible == apps.Count
                ? $"{apps.Count} apps"
                : $"{visible} of {apps.Count} apps";
        }

        // --- Filtering ---

        private bool FilterApp(object obj)
        {
            if (obj is not AppInfo app) return false;
            string filter = FilterTextBox.Text.Trim();
            if (string.IsNullOrEmpty(filter)) return true;

            return app.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || app.PackageName.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            appsView.Refresh();
            UpdateStatusText();
        }

        // --- System apps toggle ---

        private void ShowSystemAppsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            LoadApps();
        }

        // --- Refresh ---

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadApps();
        }

        // --- WSA Settings ---

        private void WsaSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Launch WSA Settings via its protocol URI
                Process.Start(new ProcessStartInfo
                {
                    FileName = "wsa-settings://",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open WSA Settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // --- Click / Long-press / Drag handling ---

        private void AppItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var app = (sender as FrameworkElement)?.DataContext as AppInfo;
            if (app == null) return;

            dragStartPoint = e.GetPosition(null);
            isDragReady = true;
            isLongPress = false;

            longPressApp = app;
            longPressTimer?.Stop();
            longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            longPressTimer.Tick += LongPressTimer_Tick;
            longPressTimer.Start();
        }

        private void AppItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            longPressTimer?.Stop();
            isDragReady = false;

            if (isLongPress)
            {
                isLongPress = false;
                return;
            }

            var app = (sender as FrameworkElement)?.DataContext as AppInfo;
            if (app != null)
            {
                LaunchApp(app);
            }
        }

        private void AppItem_MouseLeave(object sender, MouseEventArgs e)
        {
            longPressTimer?.Stop();
            isDragReady = false;
        }

        private void AppItem_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragReady || e.LeftButton != MouseButtonState.Pressed) return;

            var diff = dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            longPressTimer?.Stop();
            isDragReady = false;

            var app = (sender as FrameworkElement)?.DataContext as AppInfo;
            if (app == null) return;

            StartDrag(app, sender as DependencyObject);
        }

        private void LongPressTimer_Tick(object? sender, EventArgs e)
        {
            longPressTimer?.Stop();
            isLongPress = true;
            isDragReady = false;

            if (longPressApp != null)
            {
                ShowAppContextMenu(longPressApp);
            }
        }

        // --- App launching ---

        private void LaunchApp(AppInfo app)
        {
            try
            {
                appService.LaunchApp(app.PackageName);
                StatusText.Text = $"Launched {app.Label}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch {app.Label}:\n{ex.Message}",
                    "App Drawer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- Long-press context menu ---

        private void ShowAppContextMenu(AppInfo app)
        {
            var menu = new ContextMenu();

            var launchItem = new MenuItem { Header = "Launch" };
            launchItem.Click += (s, e) => LaunchApp(app);
            menu.Items.Add(launchItem);

            menu.Items.Add(new Separator());

            var infoItem = new MenuItem { Header = "App Info" };
            infoItem.Click += (s, e) =>
            {
                string info = appService.GetAppVersionInfo(app.PackageName);
                MessageBox.Show(info, $"App Info - {app.Label}",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            };
            menu.Items.Add(infoItem);

            var settingsItem = new MenuItem { Header = "App Settings" };
            settingsItem.Click += (s, e) =>
            {
                try
                {
                    appService.OpenAppSettings(app.PackageName);
                    StatusText.Text = $"Opened settings for {app.Label}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open settings: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            menu.Items.Add(settingsItem);

            menu.Items.Add(new Separator());

            var uninstallItem = new MenuItem { Header = "Uninstall", Foreground = System.Windows.Media.Brushes.Red };
            uninstallItem.Click += (s, e) =>
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to uninstall {app.Label}?\n\nPackage: {app.PackageName}",
                    "Uninstall App", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        string output = appService.UninstallApp(app.PackageName);
                        if (output.Contains("Success", StringComparison.OrdinalIgnoreCase))
                        {
                            apps.Remove(app);
                            UpdateStatusText();
                            StatusText.Text = $"Uninstalled {app.Label}";
                        }
                        else
                        {
                            MessageBox.Show($"Uninstall result: {output}",
                                "Uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to uninstall: {ex.Message}",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            };
            menu.Items.Add(uninstallItem);

            menu.IsOpen = true;
        }

        // --- Drag to desktop ---

        private void StartDrag(AppInfo app, DependencyObject? source)
        {
            if (source == null) return;

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "AdbExplorerDrag");
                Directory.CreateDirectory(tempDir);

                string safeLabel = string.Join("_", app.Label.Split(Path.GetInvalidFileNameChars()));
                if (string.IsNullOrEmpty(safeLabel)) safeLabel = app.PackageName;

                string shortcutPath = Path.Combine(tempDir, $"{safeLabel}.lnk");

                string? iconPath = appService.GetCachedIconPath(app.PackageName);
                ShortcutHelper.CreateAppLaunchShortcut(shortcutPath, app.PackageName,
                    app.Label, null, iconPath);

                var dataObject = new DataObject();
                var files = new System.Collections.Specialized.StringCollection();
                files.Add(shortcutPath);
                dataObject.SetFileDropList(files);

                DragDrop.DoDragDrop(source, dataObject, DragDropEffects.Copy);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Drag failed: {ex.Message}";
            }
        }

        private void CleanupDragTempFiles()
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "AdbExplorerDrag");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }
}
