using AdbExplorer.Helpers;
using AdbExplorer.Models;
using AdbExplorer.Services;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using AdbExplorer.ViewModels; // Add this

namespace AdbExplorer
{
    public partial class MainWindow : Window
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalSize(IntPtr hMem);

        private AdbService adbService;
        private FileSystemService fileSystemService;
        private FileTransferManager fileTransferManager;
        private ObservableCollection<AndroidDevice> devices;
        private ObservableCollection<FileItem> currentFiles;
        private ObservableCollection<FolderNode> rootFolders;
        private string currentPath = "/";
        private Stack<string> navigationHistory;
        private Stack<string> navigationForward;
        private Point dragStartPoint;
        private FileSystemWatcher? tempFileWatcher;
        private Dictionary<string, string> tempFileMapping;
        private bool isDragging = false;
        private bool isPreparingDrag = false;
        private Settings settings;
        private bool isRestoringPath = false;
        private bool isSyncingTree = false;
        private HashSet<string> expandedPaths = new HashSet<string>();
        private static List<MainWindow> allWindows = new List<MainWindow>();
        private static int windowCounter = 0;
        private int windowId;
        private ObservableCollection<FavoriteItem> favoritesCollection;
        private bool suppressDeviceSelectionHandling = false;
        private string? activeDeviceId;
        private bool isDeviceRefreshInProgress;
        private bool pendingDeviceRefresh;
        private bool isLoadingRootFolders = false;

        private SearchViewModel searchViewModel;

        // Column sorting state

        private string? currentSortColumn = null;
        private bool sortAscending = true;
        private string? previousSortColumn = null;
        private bool previousSortAscending = true;

        // Keep the parameterless constructor for XAML
        public MainWindow() : this(null, null)
        {
        }


        // Main constructor with parameters
        public MainWindow(string? initialDevice, string? initialPath)
        {
            InitializeComponent();
            windowId = ++windowCounter;
            allWindows.Add(this);
            UpdateWindowCount();

            InitializeServices();
            RequestDeviceRefresh(initialDevice, initialPath);

            // Add keyboard handler
            this.PreviewKeyDown += Window_PreviewKeyDown;
            // Set initial focus to file list for keyboard navigation
            this.Loaded += (s, e) => FileListView.Focus();
            // Clean up temp files when window closes
            this.Closing += (s, e) => CleanupTempFiles();
        }

        // New method for opening new windows
        private void OpenNewWindow(string? deviceId = null, string? path = null)
        {
            var selectedDevice = (DeviceTabControl.SelectedItem as TabItem)?.Tag as AndroidDevice;
            var newWindow = new MainWindow(deviceId ?? selectedDevice?.Id, path ?? currentPath);

            // Offset the new window from the current window position
            // Use typical title bar height (~32 pixels) as the base offset
            const double titleBarHeight = 32;
            double verticalOffset = titleBarHeight;
            double horizontalOffset = titleBarHeight * 1.5; // 1.5x horizontal offset

            // Calculate new position (offset down and to the right)
            double newLeft = this.Left + horizontalOffset;
            double newTop = this.Top + verticalOffset;

            // Get screen working area (excludes taskbar)
            double screenWidth = SystemParameters.WorkArea.Width;
            double screenHeight = SystemParameters.WorkArea.Height;

            // Check if there's enough room on the screen, otherwise don't offset
            if (newLeft + this.ActualWidth <= screenWidth &&
                newTop + this.ActualHeight <= screenHeight)
            {
                newWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                newWindow.Left = newLeft;
                newWindow.Top = newTop;
            }

            newWindow.Show();
        }

        // Button handler for new window
        private void NewWindowButton_Click(object sender, RoutedEventArgs e)
        {
            OpenNewWindow();
        }

        // Context menu handlers for opening in new window
        private void OpenInNewWindowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FileListView.SelectedItem is FileItem item)
            {
                if (item.IsDirectory)
                {
                    OpenNewWindow(null, item.FullPath);
                }
            }
        }

        private void TreeOpenInNewWindow_Click(object sender, RoutedEventArgs e)
        {
            if (FolderTreeView.SelectedItem is FolderNode folder)
            {
                OpenNewWindow(null, folder.FullPath);
            }
        }

        // Update window count in status bar
        private static void UpdateWindowCount()
        {
            foreach (var window in allWindows)
            {
                window.Dispatcher.Invoke(() =>
                {
                    window.WindowCountText.Text = $"{allWindows.Count} window{(allWindows.Count != 1 ? "s" : "")}";
                });
            }
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F1 - Help
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ShowHelpDialog();
                return;
            }

            // Show what keyboard shortcut was pressed (for debugging)
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                System.Diagnostics.Debug.WriteLine($"Ctrl+{e.Key} pressed");
            }

            // Ctrl+L - Sync Tree
            if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                System.Diagnostics.Debug.WriteLine("Executing Sync Tree via Ctrl+L");
                await SyncTreeWithCurrentPath();
            }

            // Ctrl+F - Focus Search
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                SearchBox.Focus();
            }

            // Delete key
            if (e.Key == Key.Delete)
            {
                if (FileListView.SelectedItems.Count > 0 &&
                    (FileListView.IsFocused || FileListView.IsKeyboardFocusWithin))
                {
                    e.Handled = true;
                    await DeleteSelectedItems();
                }
                else if (FolderTreeView.SelectedItem is FolderNode selectedFolder &&
                         (FolderTreeView.IsFocused || FolderTreeView.IsKeyboardFocusWithin))
                {
                    if (selectedFolder.FullPath != "/")
                    {
                        e.Handled = true;
                        await DeleteTreeViewItem(selectedFolder);
                    }
                }
            }

            // F2 - Rename
            if (e.Key == Key.F2)
            {
                if (FileListView.SelectedItems.Count == 1 && FileListView.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    await RenameSelectedItem();
                }
            }

            // F5 - Refresh
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await RefreshCurrentFolder();
            }

            // Ctrl+A - Select All
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (FileListView.IsFocused || FileListView.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    FileListView.SelectAll();
                }
            }

            // Ctrl+C - Copy
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (FileListView.SelectedItems.Count > 0 &&
                    (FileListView.IsFocused || FileListView.IsKeyboardFocusWithin))
                {
                    e.Handled = true;
                    CopySelectedItems();
                }
            }

            // Ctrl+V - Paste
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (FileListView.IsFocused || FileListView.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    await PasteItems();
                }
            }

            // Shift+Insert - Paste (alternative shortcut)
            if (e.Key == Key.Insert && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                if (FileListView.IsFocused || FileListView.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    await PasteItems();
                }
            }

            // Ctrl+N - New Folder
            if (e.Key == Key.N && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                await CreateNewFolder();
            }

            // Alt+Left - Back (use SystemKey for Alt combinations)
            var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;

            if (actualKey == Key.Left && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                e.Handled = true;
                if (navigationHistory.Count > 0)
                {
                    navigationForward.Push(currentPath);
                    string path = navigationHistory.Pop();
                    await NavigateToPath(path);
                }
            }

            // Alt+Right - Forward
            if (actualKey == Key.Right && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                e.Handled = true;
                if (navigationForward.Count > 0)
                {
                    navigationHistory.Push(currentPath);
                    string path = navigationForward.Pop();
                    await NavigateToPath(path);
                }
            }

            // Alt+Up - Up Directory
            if (actualKey == Key.Up && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                e.Handled = true;
                if (currentPath != "/")
                {
                    navigationHistory.Push(currentPath);
                    navigationForward.Clear();
                    string parentPath = Path.GetDirectoryName(currentPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "/";
                    await NavigateToPath(parentPath);
                }
            }

            // Backspace - Go up one directory
            if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (!(Keyboard.FocusedElement is TextBox))
                {
                    e.Handled = true;
                    if (currentPath != "/")
                    {
                        navigationHistory.Push(currentPath);
                        navigationForward.Clear();
                        string parentPath = Path.GetDirectoryName(currentPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "/";
                        await NavigateToPath(parentPath);
                    }
                }
            }

            // Enter - Open file/folder
            if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                if (FileListView.SelectedItems.Count == 1 && FileListView.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    if (FileListView.SelectedItem is FileItem item)
                    {
                        if (item.IsDirectory)
                        {
                            navigationHistory.Push(currentPath);
                            navigationForward.Clear();
                            await NavigateToPath(item.FullPath);
                        }
                        else
                        {
                            await OpenFileInWindows(item);
                        }
                    }
                }
            }
        }

        #region Command Bindings for Clipboard Operations

        private void CopyCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            // Check if focus is in a TextBox - let it handle copy natively
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (FileListView.SelectedItems.Count > 0)
            {
                CopySelectedItems();
                e.Handled = true;
            }
        }

        private void CopyCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            // Allow TextBox to handle its own copy
            if (Keyboard.FocusedElement is TextBox)
            {
                e.CanExecute = true;
                e.ContinueRouting = true;
                return;
            }

            e.CanExecute = FileListView.SelectedItems.Count > 0;
        }

        private async void PasteCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            // Check if focus is in a TextBox - let it handle paste natively
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            await PasteItems();
            e.Handled = true;
        }

        private void PasteCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            // Allow TextBox to handle its own paste
            if (Keyboard.FocusedElement is TextBox)
            {
                e.CanExecute = true;
                e.ContinueRouting = true;
                return;
            }

            e.CanExecute = Clipboard.ContainsData("AdbFiles") || Clipboard.ContainsFileDropList();
        }

        private void CutCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            // Check if focus is in a TextBox - let it handle cut natively
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            // Cut is not implemented for ADB files (would need to track cut state)
            // For now, just copy
            if (FileListView.SelectedItems.Count > 0)
            {
                CopySelectedItems();
                e.Handled = true;
            }
        }

        private void CutCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            // Allow TextBox to handle its own cut
            if (Keyboard.FocusedElement is TextBox)
            {
                e.CanExecute = true;
                e.ContinueRouting = true;
                return;
            }

            e.CanExecute = FileListView.SelectedItems.Count > 0;
        }

        private void SelectAllCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            // Check if focus is in a TextBox - let it handle select all natively
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            FileListView.SelectAll();
            e.Handled = true;
        }

        private void SelectAllCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            // Allow TextBox to handle its own select all
            if (Keyboard.FocusedElement is TextBox)
            {
                e.CanExecute = true;
                e.ContinueRouting = true;
                return;
            }

            e.CanExecute = true;
        }

        private async void DeleteCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            // Check if focus is in a TextBox - let it handle delete natively
            if (Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (FileListView.SelectedItems.Count > 0 &&
                (FileListView.IsFocused || FileListView.IsKeyboardFocusWithin))
            {
                await DeleteSelectedItems();
                e.Handled = true;
            }
            else if (FolderTreeView.SelectedItem is FolderNode selectedFolder &&
                     (FolderTreeView.IsFocused || FolderTreeView.IsKeyboardFocusWithin))
            {
                if (selectedFolder.FullPath != "/")
                {
                    await DeleteTreeViewItem(selectedFolder);
                    e.Handled = true;
                }
            }
        }

        private void DeleteCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            // Allow TextBox to handle its own delete
            if (Keyboard.FocusedElement is TextBox)
            {
                e.CanExecute = true;
                e.ContinueRouting = true;
                return;
            }

            e.CanExecute = FileListView.SelectedItems.Count > 0 ||
                          (FolderTreeView.SelectedItem is FolderNode folder && folder.FullPath != "/");
        }

        #endregion


        private void InitializeServices()
        {
            // Load settings
            settings = Settings.Load();

            adbService = new AdbService();
            adbService.DevicesChanged += AdbService_DevicesChanged;
            adbService.StartDeviceTracking();
            
            searchViewModel = new SearchViewModel();
            SearchBox.DataContext = searchViewModel;

            fileSystemService = new FileSystemService(adbService);
            fileTransferManager = new FileTransferManager(adbService, fileSystemService, this);
            devices = new ObservableCollection<AndroidDevice>();
            currentFiles = new ObservableCollection<FileItem>();
            rootFolders = new ObservableCollection<FolderNode>();
            navigationHistory = new Stack<string>();
            navigationForward = new Stack<string>();
            tempFileMapping = new Dictionary<string, string>();

            FileListView.ItemsSource = currentFiles;
            FolderTreeView.ItemsSource = rootFolders;
            
            // Initialize favorites
            LoadFavorites();

            // Restore sorting preferences
            if (!string.IsNullOrEmpty(settings.CurrentSortColumn))
            {
                currentSortColumn = settings.CurrentSortColumn;
                sortAscending = settings.SortAscending;
                previousSortColumn = settings.PreviousSortColumn;
                previousSortAscending = settings.PreviousSortAscending;
            }

            // Restore window size and position
            if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
            {
                this.Width = settings.WindowWidth;
                this.Height = settings.WindowHeight;
                this.Left = settings.WindowLeft;
                this.Top = settings.WindowTop;

                // Make sure window is on screen
                EnsureWindowIsOnScreen();
            }
        }

        private void EnsureWindowIsOnScreen()
        {
            var virtualScreenWidth = System.Windows.SystemParameters.VirtualScreenWidth;
            var virtualScreenHeight = System.Windows.SystemParameters.VirtualScreenHeight;
            var virtualScreenLeft = System.Windows.SystemParameters.VirtualScreenLeft;
            var virtualScreenTop = System.Windows.SystemParameters.VirtualScreenTop;

            if (this.Left < virtualScreenLeft)
                this.Left = virtualScreenLeft;
            if (this.Top < virtualScreenTop)
                this.Top = virtualScreenTop;
            if (this.Left + this.Width > virtualScreenLeft + virtualScreenWidth)
                this.Left = virtualScreenLeft + virtualScreenWidth - this.Width;
            if (this.Top + this.Height > virtualScreenTop + virtualScreenHeight)
                this.Top = virtualScreenTop + virtualScreenHeight - this.Height;
        }

        private DispatcherTimer? notificationTimer;
        private Brush? originalStatusTextBrush;

        private void ShowStatusNotification(string message, bool isError = false)
        {
            if (originalStatusTextBrush == null)
            {
                originalStatusTextBrush = StatusText.Foreground;
            }

            StatusText.Text = message;
            StatusText.Foreground = isError ? Brushes.Red : Brushes.Blue;
            
            notificationTimer?.Stop();
            notificationTimer = new DispatcherTimer();
            notificationTimer.Interval = TimeSpan.FromSeconds(3);
            notificationTimer.Tick += (s, e) =>
            {
                notificationTimer.Stop();
                StatusText.Foreground = originalStatusTextBrush;
                StatusText.Text = "Ready";
            };
            notificationTimer.Start();
        }

        private void AdbService_DevicesChanged(object? sender, EventArgs e)
        {
            Trace.WriteLine("MainWindow detected adb device change event.");
            Dispatcher.InvokeAsync(() => RequestDeviceRefresh());
        }

        private void RequestDeviceRefresh(string? initialDevice = null, string? initialPath = null)
        {
            Trace.WriteLine("MainWindow scheduling device refresh.");
            if (isDeviceRefreshInProgress)
            {
                pendingDeviceRefresh = true;
                return;
            }

            _ = LoadDevicesAsync(initialDevice, initialPath);
        }

        private async Task LoadDevicesAsync(string? initialDevice = null, string? initialPath = null)
        {
            isDeviceRefreshInProgress = true;
            try
            {
                StatusText.Text = "Scanning for devices...";

                var previouslySelectedDeviceId = activeDeviceId;
                var deviceList = await Task.Run(() => adbService.GetDevices());

                Trace.WriteLine($"MainWindow obtained {deviceList.Count} device(s) from adb.");
                devices.Clear();

                foreach (var device in deviceList)
                {
                    Trace.WriteLine($"MainWindow adding device {device.Id} ({device.Model})");
                    devices.Add(device);
                }

                // Update device tabs (suppress selection handling to prevent unwanted navigation)
                suppressDeviceSelectionHandling = true;
                UpdateDeviceTabs();

                if (devices.Count > 0)
                {
                    AndroidDevice? deviceToSelect = null;

                    if (!string.IsNullOrEmpty(initialDevice))
                    {
                        deviceToSelect = devices.FirstOrDefault(d => d.Id == initialDevice);
                    }

                    if (deviceToSelect == null && !string.IsNullOrEmpty(previouslySelectedDeviceId))
                    {
                        deviceToSelect = devices.FirstOrDefault(d => d.Id == previouslySelectedDeviceId);
                    }

                    if (deviceToSelect == null && !string.IsNullOrEmpty(settings.LastDevice))
                    {
                        deviceToSelect = devices.FirstOrDefault(d => d.Id == settings.LastDevice);
                    }

                    deviceToSelect ??= devices.First();

                    Trace.WriteLine($"MainWindow selecting device {deviceToSelect.Id}");

                    var isSameDevice = !string.IsNullOrEmpty(previouslySelectedDeviceId) &&
                                       deviceToSelect.Id == previouslySelectedDeviceId;

                    // Select the tab for the device (still suppressed)
                    SelectDeviceTab(deviceToSelect);

                    // Now allow selection handling, but only trigger reload if it's a different device
                    if (isSameDevice)
                    {
                        // Same device - just reset flag without triggering reload
                        suppressDeviceSelectionHandling = false;
                    }
                    else
                    {
                        // Different device - need to trigger the selection handler to load content
                        suppressDeviceSelectionHandling = false;
                        // Manually trigger what the selection handler would do
                        await HandleDeviceSelection(deviceToSelect);
                    }

                    if (!string.IsNullOrEmpty(initialPath))
                    {
                        settings.LastPath = initialPath;
                    }
                }
                else
                {
                    suppressDeviceSelectionHandling = false;
                    DeviceTabControl.SelectedIndex = -1;
                    activeDeviceId = null;
                    StatusText.Text = "No devices found";
                }

                StatusText.Text = devices.Count > 0 ? $"Found {devices.Count} device(s)" : "No devices found";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading devices: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error loading devices";
                Trace.WriteLine($"MainWindow failed to load devices: {ex.Message}");
            }
            finally
            {
                isDeviceRefreshInProgress = false;
                suppressDeviceSelectionHandling = false;  // Safety reset
                Trace.WriteLine($"MainWindow device refresh complete. Pending? {pendingDeviceRefresh}");

                if (pendingDeviceRefresh)
                {
                    pendingDeviceRefresh = false;
                    RequestDeviceRefresh();
                }
            }
        }

        private void UpdateDeviceTabs()
        {
            DeviceTabControl.Items.Clear();
            foreach (var device in devices)
            {
                var tabItem = new TabItem
                {
                    Header = device.Model,
                    Tag = device,
                    ToolTip = $"{device.Model} ({device.Id}) - {device.Status}"
                };
                DeviceTabControl.Items.Add(tabItem);
            }

            if (devices.Count == 0)
            {
                var emptyTab = new TabItem
                {
                    Header = "No devices",
                    IsEnabled = false
                };
                DeviceTabControl.Items.Add(emptyTab);
            }
        }

        private void SelectDeviceTab(AndroidDevice device)
        {
            foreach (TabItem tab in DeviceTabControl.Items)
            {
                if (tab.Tag is AndroidDevice tabDevice && tabDevice.Id == device.Id)
                {
                    DeviceTabControl.SelectedItem = tab;
                    break;
                }
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Add keyboard shortcut for sync tree
            var syncCommand = new RoutedCommand();
            var syncBinding = new KeyBinding(syncCommand, new KeyGesture(Key.L, ModifierKeys.Control));
            syncBinding.Command = new RelayCommand(async () => await SyncTreeWithCurrentPath());
            this.InputBindings.Add(syncBinding);
        }

        private async void DeviceTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Skip handling entirely if suppressed (e.g., during tab rebuilding)
            if (suppressDeviceSelectionHandling)
            {
                return;
            }

            if (DeviceTabControl.SelectedItem is not TabItem selectedTab || selectedTab.Tag is not AndroidDevice device)
            {
                activeDeviceId = null;
                return;
            }

            bool bypassReload = activeDeviceId == device.Id;

            Trace.WriteLine($"MainWindow tab selection changed to {device.Id}, bypassReload={bypassReload}");

            adbService.SetCurrentDevice(device.Id);
            activeDeviceId = device.Id;

            if (bypassReload)
            {
                return;
            }

            await HandleDeviceSelection(device);
        }

        private async Task HandleDeviceSelection(AndroidDevice device)
        {
            // Save selected device
            settings.LastDevice = device.Id;
            settings.Save();

            adbService.SetCurrentDevice(device.Id);
            activeDeviceId = device.Id;

            await LoadRootFolders();

            // Try to restore last path if it's the same device
            if (!string.IsNullOrEmpty(settings.LastPath) && device.Id == settings.LastDevice)
            {
                isRestoringPath = true;
                await RestoreLastPath(settings.LastPath);
                isRestoringPath = false;
            }
            else
            {
                await NavigateToPath("/");
            }
        }

        private async Task RestoreLastPath(string path)
        {
            try
            {
                StatusText.Text = $"Restoring {path}...";

                // First check if the path still exists
                var checkExists = await Task.Run(() =>
                {
                    var result = adbService.ExecuteShellCommand($"test -d \"{path}\" && echo 'exists' || echo 'not found'");
                    return result.Trim() == "exists";
                });

                if (!checkExists)
                {
                    StatusText.Text = "Last folder no longer exists";
                    await NavigateToPath("/");
                    return;
                }

                // Navigate to the path
                await NavigateToPath(path);

                // Expand tree to show the path
                if (path != "/" && rootFolders.Count > 0)
                {
                    await ExpandTreeToPath(path);
                }

                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error restoring path: {ex.Message}");
                StatusText.Text = "Could not restore last folder";
                await NavigateToPath("/");
            }
        }

        private async Task ExpandTreeToPath(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath) || targetPath == "/")
            {
                // For root, just select the root node
                if (rootFolders.Count > 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var rootItem = FolderTreeView.ItemContainerGenerator.ContainerFromItem(rootFolders[0]) as TreeViewItem;
                        if (rootItem != null)
                        {
                            isSyncingTree = true;
                            rootItem.IsSelected = true;
                            rootItem.BringIntoView();
                            isSyncingTree = false;
                        }
                    });
                }
                return;
            }

            var pathParts = targetPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length == 0) return;

            await Dispatcher.InvokeAsync(async () =>
            {
                // Make sure we're in sync mode
                isSyncingTree = true;

                try
                {
                    // Start from root
                    var rootNode = rootFolders[0];
                    var currentPath = "";
                    TreeViewItem? parentTreeItem = null;

                    // Get the root tree item
                    parentTreeItem = FolderTreeView.ItemContainerGenerator.ContainerFromItem(rootNode) as TreeViewItem;

                    FolderNode currentNode = rootNode;

                    foreach (var part in pathParts)
                    {
                        currentPath = currentPath == "" ? "/" + part : currentPath + "/" + part;

                        // Load children if needed
                        if (currentNode.Children.Count == 1 && string.IsNullOrEmpty(currentNode.Children[0].Name))
                        {
                            await LoadTreeNodeChildren(currentNode);

                            // Give UI time to update
                            await Task.Delay(50);

                            // Update the tree item container after loading
                            if (parentTreeItem != null)
                            {
                                parentTreeItem.UpdateLayout();
                            }
                        }

                        // Find the child node in the data
                        var childNode = currentNode.Children.FirstOrDefault(n => n.Name == part);
                        if (childNode == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Could not find node: {part} in {currentNode.FullPath}");
                            break;
                        }

                        // Expand parent if we have it
                        if (parentTreeItem != null)
                        {
                            parentTreeItem.IsExpanded = true;
                            parentTreeItem.UpdateLayout();

                            // Find the child tree item
                            var childTreeItem = parentTreeItem.ItemContainerGenerator.ContainerFromItem(childNode) as TreeViewItem;

                            // If container is not generated yet, force it
                            if (childTreeItem == null)
                            {
                                parentTreeItem.BringIntoView();
                                parentTreeItem.UpdateLayout();
                                await Task.Delay(50); // Give time for container generation

                                // Try to get the container again
                                childTreeItem = parentTreeItem.ItemContainerGenerator.ContainerFromItem(childNode) as TreeViewItem;
                            }

                            if (childTreeItem != null)
                            {
                                // If this is the target path, select it
                                if (currentPath == targetPath)
                                {
                                    childTreeItem.IsSelected = true;
                                    childTreeItem.BringIntoView();
                                }

                                parentTreeItem = childTreeItem;
                            }
                        }

                        currentNode = childNode;
                    }
                }
                finally
                {
                    // Always reset the flag
                    isSyncingTree = false;
                }
            });
        }

        private TreeViewItem? FindTreeViewItem(ItemsControl container, object item)
        {
            if (container == null) return null;

            if (container.DataContext == item)
            {
                return container as TreeViewItem;
            }

            // Search children
            for (int i = 0; i < container.Items.Count; i++)
            {
                var child = container.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (child != null)
                {
                    var result = FindTreeViewItem(child, item);
                    if (result != null) return result;
                }
            }

            return null;
        }

        private async Task LoadRootFolders()
        {
            if (isLoadingRootFolders)
            {
                System.Diagnostics.Debug.WriteLine("LoadRootFolders already in progress, skipping");
                return;
            }

            isLoadingRootFolders = true;
            try
            {
                rootFolders.Clear();
                var root = await Task.Run(() => fileSystemService.GetFolderTree("/"));
                rootFolders.Add(root);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading folders: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isLoadingRootFolders = false;
            }
        }

        private async Task NavigateToPath(string path)
        {
            try
            {
                StatusText.Text = $"Loading {path}...";
                currentPath = path;
                PathTextBox.Text = path;

                var files = await Task.Run(() => fileSystemService.GetFiles(path));

                currentFiles.Clear();

                // If no sort column is set, default to sorting by Name
                if (string.IsNullOrEmpty(currentSortColumn))
                {
                    currentSortColumn = "Name";
                    sortAscending = true;
                }

                // Add all files first
                foreach (var file in files)
                {
                    currentFiles.Add(file);
                }

                // Apply current sorting
                SortFileList();

                UpdateStatusBar();
                StatusText.Text = "Ready";

                // Save current path (but not during restoration)
                if (!isRestoringPath)
                {
                    settings.LastPath = path;
                    settings.Save();
                }

                // Update navigation buttons
                BackButton.IsEnabled = navigationHistory.Count > 0;
                ForwardButton.IsEnabled = navigationForward.Count > 0;
                UpButton.IsEnabled = path != "/";
                
                // Auto-select matching favorite if exists
                UpdateSelectedFavorite(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to {path}: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Navigation error";
            }
        }

        private void UpdateStatusBar()
        {
            ItemCountText.Text = $"{currentFiles.Count} items";
            var selected = FileListView.SelectedItems.Count;
            SelectionText.Text = selected > 0 ? $"{selected} selected" : "";
        }

        private async void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileListView.SelectedItem is FileItem item)
            {
                if (item.IsDirectory)
                {
                    navigationHistory.Push(currentPath);
                    navigationForward.Clear();
                    await NavigateToPath(item.FullPath);
                }
                else
                {
                    await OpenFileInWindows(item);
                }
            }
        }

        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (sender is GridViewColumnHeader columnHeader && columnHeader.Content != null)
            {
                string columnName = columnHeader.Content.ToString() ?? "";

                // Save previous sort state before updating
                previousSortColumn = currentSortColumn;
                previousSortAscending = sortAscending;

                // Toggle sort direction if clicking the same column
                if (currentSortColumn == columnName)
                {
                    sortAscending = !sortAscending;
                }
                else
                {
                    currentSortColumn = columnName;
                    sortAscending = true;
                }

                SortFileList();

                // Save sorting preferences to settings
                settings.CurrentSortColumn = currentSortColumn;
                settings.SortAscending = sortAscending;
                settings.PreviousSortColumn = previousSortColumn;
                settings.PreviousSortAscending = previousSortAscending;
                settings.Save();
            }
        }

        private void SortFileList()
        {
            if (currentFiles == null || currentFiles.Count == 0 || string.IsNullOrEmpty(currentSortColumn))
                return;

            var sortedList = new List<FileItem>(currentFiles);

            // Always keep directories first, then apply the column sorting
            switch (currentSortColumn)
            {
                case "Name":
                    if (sortAscending)
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenBy(f => f.Name, new NaturalStringComparer())
                                               .ToList();
                    }
                    else
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenByDescending(f => f.Name, new NaturalStringComparer())
                                               .ToList();
                    }
                    break;

                case "Size":
                    if (sortAscending)
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenBy(f => f.Size)
                                               .ToList();
                    }
                    else
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenByDescending(f => f.Size)
                                               .ToList();
                    }
                    break;

                case "Modified":
                    if (sortAscending)
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenBy(f => f.Modified)
                                               .ToList();
                    }
                    else
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenByDescending(f => f.Modified)
                                               .ToList();
                    }
                    break;

                case "Permissions":
                    if (sortAscending)
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenBy(f => f.Permissions)
                                               .ToList();
                    }
                    else
                    {
                        sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                               .ThenByDescending(f => f.Permissions)
                                               .ToList();
                    }
                    break;

                case "Type":
                    // For Type sorting, maintain the previous sort order within each type group (stable sort)
                    if (!string.IsNullOrEmpty(previousSortColumn) && previousSortColumn != "Type")
                    {
                        // Apply stable sorting - sort by Type first, then by previous sort criteria
                        if (sortAscending)
                        {
                            var query = sortedList.OrderBy(f => !f.IsDirectory)
                                                  .ThenBy(f => f.Type);

                            // Apply secondary sort based on previous column
                            switch (previousSortColumn)
                            {
                                case "Name":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Name, new NaturalStringComparer())
                                        : query.ThenByDescending(f => f.Name, new NaturalStringComparer());
                                    break;
                                case "Size":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Size)
                                        : query.ThenByDescending(f => f.Size);
                                    break;
                                case "Modified":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Modified)
                                        : query.ThenByDescending(f => f.Modified);
                                    break;
                                case "Permissions":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Permissions)
                                        : query.ThenByDescending(f => f.Permissions);
                                    break;
                                default:
                                    query = query.ThenBy(f => f.Name, new NaturalStringComparer());
                                    break;
                            }
                            sortedList = query.ToList();
                        }
                        else
                        {
                            var query = sortedList.OrderBy(f => !f.IsDirectory)
                                                  .ThenByDescending(f => f.Type);

                            // Apply secondary sort based on previous column
                            switch (previousSortColumn)
                            {
                                case "Name":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Name, new NaturalStringComparer())
                                        : query.ThenByDescending(f => f.Name, new NaturalStringComparer());
                                    break;
                                case "Size":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Size)
                                        : query.ThenByDescending(f => f.Size);
                                    break;
                                case "Modified":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Modified)
                                        : query.ThenByDescending(f => f.Modified);
                                    break;
                                case "Permissions":
                                    query = previousSortAscending
                                        ? query.ThenBy(f => f.Permissions)
                                        : query.ThenByDescending(f => f.Permissions);
                                    break;
                                default:
                                    query = query.ThenBy(f => f.Name, new NaturalStringComparer());
                                    break;
                            }
                            sortedList = query.ToList();
                        }
                    }
                    else
                    {
                        // No previous sort or previous was also Type, just sort by Type then Name
                        if (sortAscending)
                        {
                            sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                                   .ThenBy(f => f.Type)
                                                   .ThenBy(f => f.Name, new NaturalStringComparer())
                                                   .ToList();
                        }
                        else
                        {
                            sortedList = sortedList.OrderBy(f => !f.IsDirectory)
                                                   .ThenByDescending(f => f.Type)
                                                   .ThenBy(f => f.Name, new NaturalStringComparer())
                                                   .ToList();
                        }
                    }
                    break;
            }

            // Clear and repopulate the collection to update the UI
            currentFiles.Clear();
            foreach (var item in sortedList)
            {
                currentFiles.Add(item);
            }
        }

        private async Task OpenFileInWindows(FileItem file)
        {
            try
            {
                StatusText.Text = $"Opening {file.Name}...";

                // Create temp directory if not exists
                string tempDir = Path.Combine(Path.GetTempPath(), "AdbExplorer");
                Directory.CreateDirectory(tempDir);

                // Pull file from device
                string tempFile = Path.Combine(tempDir, file.Name);
                await Task.Run(() => fileSystemService.PullFile(file.FullPath, tempFile));

                // Store mapping for auto-sync
                tempFileMapping[tempFile] = file.FullPath;

                // Set up file watcher
                SetupFileWatcher(tempDir);

                // Open file
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempFile,
                    UseShellExecute = true
                });

                StatusText.Text = $"Opened {file.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error opening file";
            }
        }

        private void SetupFileWatcher(string path)
        {
            if (tempFileWatcher == null)
            {
                tempFileWatcher = new FileSystemWatcher(path);
                tempFileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                tempFileWatcher.Changed += OnTempFileChanged;
                tempFileWatcher.EnableRaisingEvents = true;
            }
        }

        private async void OnTempFileChanged(object sender, FileSystemEventArgs e)
        {
            if (tempFileMapping.ContainsKey(e.FullPath))
            {
                await Task.Delay(500); // Wait for file write to complete

                // Use discard operator since Dispatcher.InvokeAsync returns DispatcherOperation, not Task
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        StatusText.Text = $"Syncing {Path.GetFileName(e.FullPath)}...";
                        await Task.Run(() =>
                            fileSystemService.PushFile(e.FullPath, tempFileMapping[e.FullPath]));
                        StatusText.Text = $"Synced {Path.GetFileName(e.FullPath)}";
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"Sync failed: {ex.Message}";
                    }
                });
            }
        }

        // Drag and Drop implementation
        private void FileListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Check if we're clicking on a column header or gripper
            var hitTest = e.OriginalSource as DependencyObject;

            // Don't start drag if clicking on header elements
            if (IsHeaderElement(hitTest))
            {
                dragStartPoint = new Point(-1, -1); // Invalid point to prevent drag
                return;
            }

            // Only record drag start point if we're clicking on an actual item
            var item = FindAncestor<ListViewItem>(hitTest);
            if (item != null)
            {
                dragStartPoint = e.GetPosition(null);
                isDragging = false;

                // IMPORTANT: Preserve multi-selection during drag operations
                // If the clicked item is already selected, don't let the click clear the selection
                // But allow double-clicks to work (they need the event to not be handled)
                if (item.IsSelected && e.ClickCount == 1)
                {
                    // Prevent the selection from being cleared when starting a drag
                    // Only for single clicks - double clicks need to go through
                    e.Handled = true;
                }
                // If Ctrl or Shift is held, let the default multi-select behavior work
                else if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
                {
                    // If no modifier keys and clicking on unselected item, this will select only that item
                    // This is the expected behavior
                }
            }
            else
            {
                dragStartPoint = new Point(-1, -1); // Invalid point
            }
        }

        private void FileListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // If we handled the mouse down to preserve selection, we might need to handle selection change here
            var hitTest = e.OriginalSource as DependencyObject;
            var item = FindAncestor<ListViewItem>(hitTest);

            // If clicking on a selected item without dragging and no modifier keys are pressed
            if (item != null && item.IsSelected && !isDragging &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
            {
                // If this was the only selected item, do nothing
                // If multiple items were selected, select only this item (deselect others)
                if (FileListView.SelectedItems.Count > 1)
                {
                    // Clear other selections and keep only this item selected
                    FileListView.SelectedItems.Clear();
                    item.IsSelected = true;
                }
            }

            // Reset drag state
            dragStartPoint = new Point(-1, -1);
            isDragging = false;
        }

        private async void FileListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            // Prevent re-entry while already dragging or preparing to drag
            if (isDragging || isPreparingDrag) return;

            // Check if we have a valid drag start point
            if (dragStartPoint.X < 0 || dragStartPoint.Y < 0) return;

            Point mousePos = e.GetPosition(null);
            Vector diff = dragStartPoint - mousePos;

            if (e.LeftButton == MouseButtonState.Pressed &&
                (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                // Additional check: make sure we're not on a header element
                var hitTest = e.OriginalSource as DependencyObject;
                if (IsHeaderElement(hitTest))
                {
                    return;
                }

                // Make sure we're dragging from an actual list item
                var item = FindAncestor<ListViewItem>(hitTest);
                if (item == null)
                {
                    return;
                }

                var selectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
                if (selectedItems.Count > 0)
                {
                    isPreparingDrag = true;
                    isDragging = true;

                    try
                    {
                        await StartDragDropOperation(selectedItems);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error during drag operation: {ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        StatusText.Text = "Drag operation failed";
                    }
                    finally
                    {
                        isPreparingDrag = false;
                        isDragging = false;
                        dragStartPoint = new Point(-1, -1); // Reset
                    }
                }
            }
        }

        private bool IsHeaderElement(DependencyObject? element)
        {
            if (element == null) return false;

            // Check if it's a GridViewColumnHeader or Thumb (resize gripper)
            while (element != null)
            {
                // GridViewColumnHeader is in System.Windows.Controls
                if (element is System.Windows.Controls.GridViewColumnHeader ||
                    element is System.Windows.Controls.Primitives.Thumb ||
                    element.GetType().Name == "GridViewColumnHeaderChrome")
                {
                    return true;
                }

                // Stop at ListView to avoid going too far up
                if (element == FileListView)
                    break;

                element = VisualTreeHelper.GetParent(element);
            }

            return false;
        }

        private async Task StartDragDropOperation(List<FileItem> selectedItems)
        {
            // Change cursor to wait immediately to show something is happening
            Mouse.OverrideCursor = Cursors.Wait;

            StatusText.Text = "Preparing files for drag...";

            string dragTempDir = Path.Combine(Path.GetTempPath(), "AdbExplorer", "DragDrop", Guid.NewGuid().ToString());
            Directory.CreateDirectory(dragTempDir);

            var filesToDrag = new System.Collections.Specialized.StringCollection();

            // Collect all files to download
            var downloadList = new List<(string remotePath, string localPath)>();
            foreach (var item in selectedItems)
            {
                if (item.IsDirectory)
                {
                    // For directories, collect all files recursively
                    CollectDirectoryFilesForDragDownload(item.FullPath, Path.Combine(dragTempDir, item.Name), downloadList);
                }
                else
                {
                    string localPath = Path.Combine(dragTempDir, item.Name);
                    downloadList.Add((item.FullPath, localPath));
                }
            }

            bool success = false;

            // For large batches, show progress panel during preparation
            if (downloadList.Count >= 4)
            {
                StatusText.Text = $"Preparing {downloadList.Count} files for drag...";

                // Show progress panel for the download preparation
                success = await fileTransferManager.DownloadFilesAsync(downloadList, true);

                // Add successfully downloaded files to drag collection
                foreach (var (remotePath, localPath) in downloadList)
                {
                    if (File.Exists(localPath) || Directory.Exists(localPath))
                    {
                        filesToDrag.Add(localPath);
                    }
                }
            }
            else
            {
                // For small batches, use quick download without progress panel
                await Task.Run(async () =>
                {
                    int fileCount = 0;
                    foreach (var (remotePath, localPath) in downloadList)
                    {
                        try
                        {
                            fileCount++;
                            await Dispatcher.InvokeAsync(() =>
                                StatusText.Text = $"Preparing file {fileCount}/{downloadList.Count}: {Path.GetFileName(remotePath)}");

                            // Create directory if needed
                            string localDir = Path.GetDirectoryName(localPath);
                            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                            {
                                Directory.CreateDirectory(localDir);
                            }

                            fileSystemService.PullFile(remotePath, localPath);

                            if (File.Exists(localPath))
                            {
                                filesToDrag.Add(localPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to pull {remotePath}: {ex.Message}");
                        }
                    }
                });
                success = filesToDrag.Count > 0;
            }

            // Restore normal cursor before starting drag
            Mouse.OverrideCursor = null;

            if (filesToDrag.Count > 0)
            {
                StatusText.Text = $"Dragging {filesToDrag.Count} item(s)...";

                var dragData = new DataObject();
                dragData.SetData("AdbFiles", selectedItems);
                dragData.SetFileDropList(filesToDrag);
                dragData.SetData("TempDragPath", dragTempDir);
                dragData.SetData("SourceWindowId", this.windowId);

                // This starts the actual drag operation - the cursor will change and Windows will handle the drop
                var result = DragDrop.DoDragDrop(FileListView, dragData, DragDropEffects.Copy);

                // Clean up temp files after drag completes
                _ = Task.Run(() => CleanupDragTempFiles(dragTempDir));
                StatusText.Text = "Ready";
            }
            else
            {
                StatusText.Text = $"Failed to prepare files for drag";
                try
                {
                    if (Directory.Exists(dragTempDir))
                        Directory.Delete(dragTempDir, true);
                }
                catch { }
            }
        }

        private void CollectDirectoryFilesForDragDownload(string remoteDir, string localDir, List<(string, string)> downloadList)
        {
            try
            {
                // Get files in remote directory
                var files = fileSystemService.GetFiles(remoteDir);

                foreach (var file in files)
                {
                    string localPath = Path.Combine(localDir, file.Name);

                    if (file.IsDirectory)
                    {
                        // Recursively collect subdirectory files
                        CollectDirectoryFilesForDragDownload(file.FullPath, localPath, downloadList);
                    }
                    else
                    {
                        downloadList.Add((file.FullPath, localPath));
                    }
                }
            }
            catch
            {
                // If we can't list directory contents, treat it as a single item
                downloadList.Add((remoteDir, localDir));
            }
        }


        private async Task PullDirectoryContents(string remotePath, string localPath)
        {
            try
            {
                var files = await Task.Run(() => fileSystemService.GetFiles(remotePath));

                foreach (var file in files)
                {
                    if (file.IsDirectory)
                    {
                        string subDir = Path.Combine(localPath, file.Name);
                        Directory.CreateDirectory(subDir);
                        await PullDirectoryContents(file.FullPath, subDir);
                    }
                    else
                    {
                        string localFile = Path.Combine(localPath, file.Name);
                        await Task.Run(() => fileSystemService.PullFile(file.FullPath, localFile));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error pulling directory {remotePath}: {ex.Message}");
            }
        }


        // Method to save the current tree expansion state
        private void SaveTreeExpansionState()
        {
            expandedPaths.Clear();
            if (rootFolders.Count > 0)
            {
                SaveNodeExpansionState(rootFolders[0], FolderTreeView);
            }
        }

        // Recursively save which nodes are expanded
        private void SaveNodeExpansionState(FolderNode node, ItemsControl container)
        {
            var treeViewItem = container.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (treeViewItem != null && treeViewItem.IsExpanded)
            {
                expandedPaths.Add(node.FullPath);

                // Check children
                foreach (var child in node.Children)
                {
                    if (!string.IsNullOrEmpty(child.Name)) // Skip dummy nodes
                    {
                        SaveNodeExpansionState(child, treeViewItem);
                    }
                }
            }
        }

        // Method to restore the tree expansion state
        private async Task RestoreTreeExpansionState()
        {
            if (rootFolders.Count == 0 || expandedPaths.Count == 0)
                return;

            await Dispatcher.InvokeAsync(async () =>
            {
                // Give the tree time to generate containers
                await Task.Delay(50);

                await RestoreNodeExpansionState(rootFolders[0], FolderTreeView);
            });
        }

        // Recursively restore expansion state
        private async Task RestoreNodeExpansionState(FolderNode node, ItemsControl container)
        {
            if (expandedPaths.Contains(node.FullPath))
            {
                var treeViewItem = container.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (treeViewItem != null)
                {
                    // Load children if needed
                    if (node.Children.Count == 1 && string.IsNullOrEmpty(node.Children[0].Name))
                    {
                        await LoadTreeNodeChildren(node);
                        await Task.Delay(50); // Give time for UI update
                    }

                    treeViewItem.IsExpanded = true;
                    treeViewItem.UpdateLayout();

                    // Restore children
                    foreach (var child in node.Children)
                    {
                        if (!string.IsNullOrEmpty(child.Name))
                        {
                            await RestoreNodeExpansionState(child, treeViewItem);
                        }
                    }
                }
            }
        }

        private async void CleanupDragTempFiles(string tempPath)
        {
            // Wait a bit to ensure Windows has finished with the files
            await Task.Delay(1000);

            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                }
            }
            catch
            {
                // Silent cleanup failure - files will be cleaned by system temp cleanup
            }
        }

        private void FileListView_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || 
                e.Data.GetDataPresent("AdbFiles") ||
                e.Data.GetDataPresent("FileGroupDescriptor") ||
                e.Data.GetDataPresent("FileGroupDescriptorW"))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void FileListView_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true; // Prevent event bubbling to Window_Drop
            try
            {
                // Determine the drop target (folder or current directory)
                string targetPath = currentPath;

                // Check if we dropped on a specific folder in the list
                var hitTest = VisualTreeHelper.HitTest(FileListView, e.GetPosition(FileListView));
                if (hitTest != null)
                {
                    var listViewItem = FindAncestor<ListViewItem>(hitTest.VisualHit);
                    if (listViewItem != null && listViewItem.DataContext is FileItem targetItem)
                    {
                        if (targetItem.IsDirectory)
                        {
                            targetPath = targetItem.FullPath;
                            StatusText.Text = $"Dropping into {targetItem.Name}...";
                        }
                    }
                }

                // Check for regular file drops first (prioritize over virtual files)
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                    // Check if this is an internal AdbExplorer drag
                    bool isFromAdbExplorer = e.Data.GetDataPresent("AdbFiles");
                    var sourceWindowId = e.Data.GetData("SourceWindowId") as int?;

                    if (isFromAdbExplorer && sourceWindowId == this.windowId)
                    {
                        // Internal drag within same window
                        var adbFiles = e.Data.GetData("AdbFiles") as List<FileItem>;
                        if (adbFiles != null)
                        {
                            // Check if trying to copy to same directory
                            if (adbFiles.Any(f => Path.GetDirectoryName(f.FullPath.Replace('\\', '/'))?.Replace('\\', '/') == targetPath))
                            {
                                StatusText.Text = "Cannot copy to the same directory";
                                return;
                            }

                            await HandleInternalDrop(adbFiles, targetPath);
                        }
                    }
                    else
                    {
                        // External drop or from another AdbExplorer window
                        await HandleExternalFileDrop(files, targetPath);
                    }
                }
                // Check for Outlook virtual files only if no regular FileDrop
                else if (e.Data.GetDataPresent("FileGroupDescriptor") || e.Data.GetDataPresent("FileGroupDescriptorW"))
                {
                    await HandleOutlookDrop(e.Data, targetPath);
                }
                else if (e.Data.GetDataPresent("AdbFiles"))
                {
                    // Internal drag without FileDrop
                    var sourceWindowId = e.Data.GetData("SourceWindowId") as int?;
                    if (sourceWindowId == this.windowId)
                    {
                        var adbFiles = e.Data.GetData("AdbFiles") as List<FileItem>;
                        if (adbFiles != null)
                        {
                            await HandleInternalDrop(adbFiles, targetPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during drop operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Drop operation failed";
            }
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T)
                {
                    return (T)current;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private async Task HandleInternalDrop(List<FileItem> adbFiles, string targetPath)
        {
            var result = MessageBox.Show($"Copy {adbFiles.Count} item(s) to {targetPath}?", "Copy Files",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                foreach (var file in adbFiles)
                {
                    try
                    {
                        StatusText.Text = $"Copying {file.Name}...";
                        string destPath = targetPath + "/" + file.Name;

                        var sourceEscaped = EscapeForShell(file.FullPath);
                        var destEscaped = EscapeForShell(destPath);

                        await Task.Run(() =>
                        {
                            if (file.IsDirectory)
                            {
                                adbService.ExecuteShellCommand($"cp -r {sourceEscaped} {destEscaped}");
                                // Set permissions recursively for directories (770 for dirs, 660 for files)
                                try
                                {
                                    adbService.ExecuteShellCommand($"chmod -R 770 {destEscaped}");
                                    adbService.ExecuteShellCommand($"find {destEscaped} -type f -exec chmod 660 {{}} \\;");
                                }
                                catch { /* Ignore permission errors */ }
                            }
                            else
                            {
                                adbService.ExecuteShellCommand($"cp {sourceEscaped} {destEscaped}");
                                // Set permissions to 660 for copied file
                                try
                                {
                                    adbService.ExecuteShellCommand($"chmod 660 {destEscaped}");
                                }
                                catch { /* Ignore permission errors */ }
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error copying {file.Name}: {ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                await RefreshCurrentFolder();
                StatusText.Text = "Copy completed";
            }
        }

        private async Task HandleExternalFileDrop(string[] files, string targetPath)
        {
            // Check for existing files and get user confirmation if needed
            var existingFiles = new List<string>();
            var existingFilesInTarget = new HashSet<string>();

            // Get current files in target directory
            try
            {
                var targetFiles = await Task.Run(() => fileSystemService.GetFiles(targetPath));
                foreach (var targetFile in targetFiles)
                {
                    existingFilesInTarget.Add(targetFile.Name.ToLowerInvariant());
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error checking target directory: {ex.Message}";
                return;
            }

            // Check which files would be overwritten
            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                if (existingFilesInTarget.Contains(fileName.ToLowerInvariant()))
                {
                    existingFiles.Add(fileName);
                }
            }

            // Show overwrite confirmation dialog if there are conflicts
            if (existingFiles.Count > 0)
            {
                // Bring window to foreground before showing dialog
                this.Activate();
                this.Topmost = true;
                this.Topmost = false; // Reset topmost to avoid staying on top permanently

                string fileList = existingFiles.Count <= 5
                    ? string.Join("\n", existingFiles)
                    : string.Join("\n", existingFiles.Take(5)) + $"\n... and {existingFiles.Count - 5} more";

                string message = existingFiles.Count == 1
                    ? $"The following file already exists:\n\n{fileList}\n\nDo you want to overwrite it?"
                    : $"The following files already exist:\n\n{fileList}\n\nDo you want to overwrite them?";

                var result = MessageBox.Show(this, message, "Overwrite Files",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    StatusText.Text = "Upload cancelled";
                    return;
                }
            }

            // Prepare file list for upload
            var uploadList = new List<(string localPath, string remotePath)>();

            foreach (string file in files)
            {
                if (Directory.Exists(file))
                {
                    // Collect all files from directory recursively
                    CollectDirectoryFiles(file, targetPath, uploadList);
                }
                else if (File.Exists(file))
                {
                    string destPath = targetPath == "/"
                        ? "/" + Path.GetFileName(file)
                        : targetPath + "/" + Path.GetFileName(file);
                    uploadList.Add((file, destPath));
                }
            }

            // Use FileTransferManager for batch operations (4+ files) or traditional method for smaller batches
            bool success;
            if (uploadList.Count >= 4)
            {
                success = await fileTransferManager.UploadFilesAsync(uploadList, true);
            }
            else
            {
                // Traditional upload for small batches
                StatusText.Text = $"Uploading {files.Length} item(s) to {targetPath}...";
                success = await UploadFilesTraditional(uploadList);
            }

            await RefreshCurrentFolder();

            if (success)
            {
                StatusText.Text = $"Uploaded {uploadList.Count} item(s)";
            }
            else
            {
                StatusText.Text = $"Upload completed with some errors";
            }
        }

        private void CollectDirectoryFiles(string localDir, string remoteDir, List<(string, string)> uploadList)
        {
            string dirName = Path.GetFileName(localDir);
            string remoteDirPath = remoteDir == "/" ? "/" + dirName : remoteDir + "/" + dirName;

            // Add all files in the directory
            foreach (string file in Directory.GetFiles(localDir))
            {
                string remotePath = remoteDirPath + "/" + Path.GetFileName(file);
                uploadList.Add((file, remotePath));
            }

            // Recursively add subdirectories
            foreach (string subDir in Directory.GetDirectories(localDir))
            {
                CollectDirectoryFiles(subDir, remoteDirPath, uploadList);
            }
        }

        private async Task<bool> UploadFilesTraditional(List<(string localPath, string remotePath)> files)
        {
            int uploadedCount = 0;
            var errors = new List<string>();
            var uploadedFiles = new List<string>();

            // Upload all files without setting permissions
            foreach (var (localPath, remotePath) in files)
            {
                try
                {
                    StatusText.Text = $"Uploading {Path.GetFileName(localPath)}...";

                    // Create directory if needed
                    string remoteDir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(remoteDir))
                    {
                        await Task.Run(() => fileSystemService.CreateFolder(remoteDir));
                    }

                    await Task.Run(() => fileSystemService.PushFile(localPath, remotePath, false));
                    uploadedFiles.Add(remotePath);
                    uploadedCount++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(localPath)}: {ex.Message}");
                }
            }

            // Set permissions for all uploaded files
            if (uploadedFiles.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var filePath in uploadedFiles)
                    {
                        fileSystemService.SetFilePermissions(filePath, "660");
                    }
                });
            }

            if (errors.Count > 0)
            {
                string errorMessage = "Some items could not be uploaded:\n\n" +
                                     string.Join("\n", errors.Take(5));
                if (errors.Count > 5)
                    errorMessage += $"\n... and {errors.Count - 5} more";

                MessageBox.Show(errorMessage, "Upload Errors",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return errors.Count == 0;
        }

        private async Task UploadDirectory(string localPath, string remotePath)
        {
            string dirName = Path.GetFileName(localPath);
            string remoteDir = remotePath + "/" + dirName;

            // Create directory on device
            await Task.Run(() => fileSystemService.CreateFolder(remoteDir));

            var uploadedFiles = new List<string>();

            // Upload all files in the directory without setting permissions yet
            foreach (string file in Directory.GetFiles(localPath))
            {
                string destPath = remoteDir + "/" + Path.GetFileName(file);
                await Task.Run(() => fileSystemService.PushFile(file, destPath, false));
                uploadedFiles.Add(destPath);
            }

            // Set permissions for all files in batch
            if (uploadedFiles.Count > 0)
            {
                await Task.Run(() =>
                {
                    foreach (var filePath in uploadedFiles)
                    {
                        fileSystemService.SetFilePermissions(filePath, "660");
                    }
                });
            }

            // Recursively upload subdirectories
            foreach (string subDir in Directory.GetDirectories(localPath))
            {
                await UploadDirectory(subDir, remoteDir);
            }
        }

        private async Task HandleOutlookDrop(IDataObject dataObject, string targetPath)
        {
            try
            {
                StatusText.Text = "Processing Outlook files...";

                // Get file descriptor to get file names
                string[] fileNames = null;
                
                // Try Unicode version first
                if (dataObject.GetDataPresent("FileGroupDescriptorW"))
                {
                    var descriptorStream = dataObject.GetData("FileGroupDescriptorW") as MemoryStream;
                    if (descriptorStream != null)
                    {
                        fileNames = GetFileNamesFromDescriptorW(descriptorStream);
                    }
                }
                // Fall back to ANSI version
                else if (dataObject.GetDataPresent("FileGroupDescriptor"))
                {
                    var descriptorStream = dataObject.GetData("FileGroupDescriptor") as MemoryStream;
                    if (descriptorStream != null)
                    {
                        fileNames = GetFileNamesFromDescriptor(descriptorStream);
                    }
                }

                if (fileNames == null || fileNames.Length == 0)
                {
                    StatusText.Text = "No files found in Outlook drop";
                    return;
                }

                // Process each file
                int uploadedCount = 0;
                var errors = new List<string>();
                var tempDir = Path.Combine(Path.GetTempPath(), "AdbExplorer", "OutlookDrop", Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);

                for (int i = 0; i < fileNames.Length; i++)
                {
                    try
                    {
                        StatusText.Text = $"Processing {fileNames[i]}...";

                        // Get file contents using COM interop
                        MemoryStream fileContents = null;
                        try
                        {
                            // Get the IDataObject as COM object to access indexed FileContents
                            var comDataObject = dataObject as System.Runtime.InteropServices.ComTypes.IDataObject;
                            if (comDataObject != null)
                            {
                                var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
                                {
                                    cfFormat = (short)DataFormats.GetDataFormat("FileContents").Id,
                                    dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
                                    lindex = i,
                                    ptd = IntPtr.Zero,
                                    tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM |
                                            System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL
                                };

                                System.Runtime.InteropServices.ComTypes.STGMEDIUM medium;
                                comDataObject.GetData(ref format, out medium);

                                if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM)
                                {
                                    var iStream = (System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                                    var buffer = new byte[1024];
                                    fileContents = new MemoryStream();
                                    int bytesRead;
                                    do
                                    {
                                        iStream.Read(buffer, buffer.Length, IntPtr.Zero);
                                        bytesRead = Marshal.ReadInt32(IntPtr.Zero);
                                        if (bytesRead > 0)
                                            fileContents.Write(buffer, 0, bytesRead);
                                    } while (bytesRead > 0);
                                    fileContents.Position = 0;
                                }
                                else if (medium.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL)
                                {
                                    var ptr = Marshal.ReadIntPtr(medium.unionmember);
                                    var size = (int)GlobalSize(ptr);
                                    var data = new byte[size];
                                    Marshal.Copy(ptr, data, 0, size);
                                    fileContents = new MemoryStream(data);
                                }

                                Marshal.ReleaseComObject(medium.pUnkForRelease);
                            }
                        }
                        catch
                        {
                            // Fall back to trying without index (may work for single file)
                            fileContents = dataObject.GetData("FileContents") as MemoryStream;
                        }
                        
                        if (fileContents != null)
                        {
                            // Save to temp file
                            string tempFile = Path.Combine(tempDir, fileNames[i]);
                            using (var fs = new FileStream(tempFile, FileMode.Create))
                            {
                                fileContents.CopyTo(fs);
                            }
                            fileContents.Dispose();

                            // Upload to device
                            string remotePath = targetPath + "/" + fileNames[i];
                            await Task.Run(() => fileSystemService.PushFile(tempFile, remotePath, false));
                            
                            // Set permissions
                            await Task.Run(() => fileSystemService.SetFilePermissions(remotePath, "660"));
                            
                            uploadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{fileNames[i]}: {ex.Message}");
                    }
                }

                // Clean up temp directory
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch { /* Ignore cleanup errors */ }

                // Refresh and show status
                await RefreshCurrentFolder();

                if (errors.Count == 0)
                {
                    StatusText.Text = $"Uploaded {uploadedCount} file(s) from Outlook";
                }
                else
                {
                    StatusText.Text = $"Uploaded {uploadedCount} file(s), {errors.Count} error(s)";
                    if (errors.Count > 0)
                    {
                        string errorMessage = "Some files could not be uploaded:\n\n" +
                                             string.Join("\n", errors.Take(5));
                        if (errors.Count > 5)
                            errorMessage += $"\n... and {errors.Count - 5} more";

                        MessageBox.Show(errorMessage, "Upload Errors",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error handling Outlook drop: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Outlook drop operation failed";
            }
        }

        private string[] GetFileNamesFromDescriptorW(MemoryStream stream)
        {
            var fileNames = new List<string>();
            stream.Position = 0;
            
            using (var reader = new BinaryReader(stream))
            {
                // Read the count of items
                int count = reader.ReadInt32();
                
                // Skip to the file descriptor array (after count field)
                for (int i = 0; i < count; i++)
                {
                    // Read FILEDESCRIPTORW structure
                    reader.ReadInt32(); // dwFlags
                    reader.ReadBytes(16); // clsid
                    reader.ReadBytes(8); // sizel
                    reader.ReadBytes(8); // pointl
                    reader.ReadInt32(); // dwFileAttributes
                    reader.ReadBytes(8); // ftCreationTime
                    reader.ReadBytes(8); // ftLastAccessTime
                    reader.ReadBytes(8); // ftLastWriteTime
                    reader.ReadInt32(); // nFileSizeHigh
                    reader.ReadInt32(); // nFileSizeLow
                    
                    // Read filename (520 bytes for wide char MAX_PATH)
                    byte[] nameBytes = reader.ReadBytes(520);
                    string fileName = System.Text.Encoding.Unicode.GetString(nameBytes);
                    int nullIndex = fileName.IndexOf('\0');
                    if (nullIndex >= 0)
                        fileName = fileName.Substring(0, nullIndex);
                    
                    if (!string.IsNullOrEmpty(fileName))
                        fileNames.Add(fileName);
                }
            }
            
            return fileNames.ToArray();
        }

        private string[] GetFileNamesFromDescriptor(MemoryStream stream)
        {
            var fileNames = new List<string>();
            stream.Position = 0;
            
            using (var reader = new BinaryReader(stream))
            {
                // Read the count of items
                int count = reader.ReadInt32();
                
                // Skip to the file descriptor array (after count field)
                for (int i = 0; i < count; i++)
                {
                    // Read FILEDESCRIPTORA structure
                    reader.ReadInt32(); // dwFlags
                    reader.ReadBytes(16); // clsid
                    reader.ReadBytes(8); // sizel
                    reader.ReadBytes(8); // pointl
                    reader.ReadInt32(); // dwFileAttributes
                    reader.ReadBytes(8); // ftCreationTime
                    reader.ReadBytes(8); // ftLastAccessTime
                    reader.ReadBytes(8); // ftLastWriteTime
                    reader.ReadInt32(); // nFileSizeHigh
                    reader.ReadInt32(); // nFileSizeLow
                    
                    // Read filename (260 bytes for ANSI MAX_PATH)
                    byte[] nameBytes = reader.ReadBytes(260);
                    string fileName = System.Text.Encoding.Default.GetString(nameBytes);
                    int nullIndex = fileName.IndexOf('\0');
                    if (nullIndex >= 0)
                        fileName = fileName.Substring(0, nullIndex);
                    
                    if (!string.IsNullOrEmpty(fileName))
                        fileNames.Add(fileName);
                }
            }
            
            return fileNames.ToArray();
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetDataPresent("FileGroupDescriptor") ||
                e.Data.GetDataPresent("FileGroupDescriptorW"))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetDataPresent("FileGroupDescriptor") ||
                e.Data.GetDataPresent("FileGroupDescriptorW"))
            {
                FileListView_Drop(sender, e);
            }
        }

        // Navigation buttons
        private async void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (navigationHistory.Count > 0)
            {
                navigationForward.Push(currentPath);
                string path = navigationHistory.Pop();
                await NavigateToPath(path);
            }
        }

        private async void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (navigationForward.Count > 0)
            {
                navigationHistory.Push(currentPath);
                string path = navigationForward.Pop();
                await NavigateToPath(path);
            }
        }

        private async void UpButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentPath != "/")
            {
                navigationHistory.Push(currentPath);
                navigationForward.Clear();
                string parentPath = Path.GetDirectoryName(currentPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "/";
                await NavigateToPath(parentPath);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCurrentFolder();
        }

        private async void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            navigationHistory.Push(currentPath);
            navigationForward.Clear();
            await NavigateToPath("/");
        }

        private async Task RefreshCurrentFolder()
        {
            // Save tree state before refresh
            SaveTreeExpansionState();

            // Only refresh the file list, not the tree
            await NavigateToPath(currentPath);

            // Optional: Update only the current node in the tree if needed
            // This is lighter than reloading the entire tree
            await RefreshCurrentTreeNode();

            // Restore tree state
            await RestoreTreeExpansionState();
        }

        private async Task RefreshCurrentTreeNode()
        {
            if (string.IsNullOrEmpty(currentPath) || rootFolders.Count == 0)
                return;

            try
            {
                // Find the current node in the tree
                var currentNode = FindNodeByPath(rootFolders[0], currentPath);
                if (currentNode != null && currentNode.Children.Count > 0)
                {
                    // Only refresh if it's been expanded (has real children, not dummy)
                    if (!(currentNode.Children.Count == 1 && string.IsNullOrEmpty(currentNode.Children[0].Name)))
                    {
                        await LoadTreeNodeChildren(currentNode);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error refreshing tree node: {ex.Message}");
            }
        }

        // Helper to find a node by path
        private FolderNode? FindNodeByPath(FolderNode root, string path)
        {
            if (root.FullPath == path)
                return root;

            foreach (var child in root.Children)
            {
                if (!string.IsNullOrEmpty(child.Name)) // Skip dummy nodes
                {
                    var found = FindNodeByPath(child, path);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private async void PathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                navigationHistory.Push(currentPath);
                navigationForward.Clear();
                await NavigateToPath(PathTextBox.Text);
            }
        }

        private async void PathTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is not TextBox textBox)
                    return;

                // Get the character index at the mouse position
                int charIndex = textBox.GetCharacterIndexFromPoint(e.GetPosition(textBox), true);
                if (charIndex < 0 || charIndex >= textBox.Text.Length)
                    return;

                string fullPath = textBox.Text;
                if (string.IsNullOrEmpty(fullPath))
                    return;

                // Split the path by '/' separator
                var segments = fullPath.Split('/');

                // Calculate which segment was clicked
                int currentPos = 0;
                string targetPath = "";

                for (int i = 0; i < segments.Length; i++)
                {
                    string segment = segments[i];
                    int segmentStart = currentPos;
                    int segmentEnd = currentPos + segment.Length;

                    // Check if the click was within this segment
                    if (charIndex >= segmentStart && charIndex < segmentEnd && !string.IsNullOrEmpty(segment))
                    {
                        // Build the target path up to and including this segment
                        if (i == 0 && segment == "")
                        {
                            // Root segment
                            targetPath = "/";
                        }
                        else
                        {
                            // Build path from beginning up to this segment
                            for (int j = 0; j <= i; j++)
                            {
                                if (j == 0 && segments[j] == "")
                                {
                                    targetPath = "/";
                                }
                                else if (j == 0)
                                {
                                    targetPath = segments[j];
                                }
                                else
                                {
                                    // If targetPath is "/", don't add another slash
                                    if (targetPath == "/")
                                    {
                                        targetPath += segments[j];
                                    }
                                    else
                                    {
                                        targetPath += "/" + segments[j];
                                    }
                                }
                            }
                        }
                        break;
                    }

                    // Move to next segment (including the separator)
                    currentPos += segment.Length + 1; // +1 for the '/' separator
                }

                // Navigate to the target path if we found one
                if (!string.IsNullOrEmpty(targetPath) && targetPath != currentPath)
                {
                    navigationHistory.Push(currentPath);
                    navigationForward.Clear();
                    await NavigateToPath(targetPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error handling path double-click: {ex.Message}");
            }
        }

        private async void GoButton_Click(object sender, RoutedEventArgs e)
        {
            navigationHistory.Push(currentPath);
            navigationForward.Clear();
            await NavigateToPath(PathTextBox.Text);
        }

        private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (isSyncingTree) return;

            if (e.NewValue is FolderNode folder)
            {
                try
                {
                    navigationHistory.Push(currentPath);
                    navigationForward.Clear();
                    await NavigateToPath(folder.FullPath);
                }
                catch (Exception ex)
                {
                    // Handle navigation failure - show in right panel
                    ShowAccessError(folder.FullPath, ex.Message);
                }
            }
        }

        private void FolderTreeView_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || 
                e.Data.GetDataPresent("AdbFiles") ||
                e.Data.GetDataPresent("FileGroupDescriptor") ||
                e.Data.GetDataPresent("FileGroupDescriptorW"))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void FolderTreeView_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            try
            {
                // Find the target folder from the drop position
                string targetPath = currentPath; // Default to current path
                
                var hitTest = VisualTreeHelper.HitTest(FolderTreeView, e.GetPosition(FolderTreeView));
                if (hitTest != null)
                {
                    var treeViewItem = FindAncestor<TreeViewItem>(hitTest.VisualHit);
                    if (treeViewItem != null && treeViewItem.DataContext is FolderNode targetFolder)
                    {
                        targetPath = targetFolder.FullPath;
                        StatusText.Text = $"Dropping into {targetFolder.Name}...";
                    }
                }

                // Handle the drop using existing logic
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    bool isFromAdbExplorer = e.Data.GetDataPresent("AdbFiles");
                    var sourceWindowId = e.Data.GetData("SourceWindowId") as int?;

                    if (isFromAdbExplorer && sourceWindowId == this.windowId)
                    {
                        // Internal drag within same window
                        var adbFiles = e.Data.GetData("AdbFiles") as List<FileItem>;
                        if (adbFiles != null)
                        {
                            await HandleInternalDrop(adbFiles, targetPath);
                        }
                    }
                    else
                    {
                        // External drop or from another AdbExplorer window
                        await HandleExternalFileDrop(files, targetPath);
                    }
                }
                else if (e.Data.GetDataPresent("FileGroupDescriptor") || e.Data.GetDataPresent("FileGroupDescriptorW"))
                {
                    await HandleOutlookDrop(e.Data, targetPath);
                }
                else if (e.Data.GetDataPresent("AdbFiles"))
                {
                    // Internal drag without FileDrop
                    var sourceWindowId = e.Data.GetData("SourceWindowId") as int?;
                    if (sourceWindowId == this.windowId)
                    {
                        var adbFiles = e.Data.GetData("AdbFiles") as List<FileItem>;
                        if (adbFiles != null)
                        {
                            await HandleInternalDrop(adbFiles, targetPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during drop operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Drop operation failed";
            }
        }

        // Context menu handlers
        private async void NewFolderButton_Click(object sender, RoutedEventArgs e)
        {
            await CreateNewFolder();
        }

        private async void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await CreateNewFolder();
        }

        private async Task CreateNewFolder()
        {
            var dialog = new InputDialog("New Folder", "Enter folder name:");
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Save tree state
                    SaveTreeExpansionState();

                    string newPath = currentPath + "/" + dialog.ResponseText;
                    await Task.Run(() => fileSystemService.CreateFolder(newPath));

                    // Only refresh current folder
                    await NavigateToPath(currentPath);

                    // Update tree for current node
                    await RefreshParentTreeNode(currentPath);

                    // Restore tree state
                    await RestoreTreeExpansionState();

                    StatusText.Text = $"Created folder: {dialog.ResponseText}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error creating folder: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            await DeleteSelectedItems();
        }

        private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await DeleteSelectedItems();
        }

        private void CopyFileNameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
            if (selectedItems.Count == 0) return;

            // If multiple items selected, join with newlines
            var fileNames = selectedItems.Select(item => item.Name);
            var text = string.Join(Environment.NewLine, fileNames);

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyFullPathMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
            if (selectedItems.Count == 0) return;

            // If multiple items selected, join with newlines
            var fullPaths = selectedItems.Select(item => item.FullPath);
            var text = string.Join(Environment.NewLine, fullPaths);

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeleteSelectedItems()
        {
            var selectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
            if (selectedItems.Count == 0) return;

            // Build confirmation message
            string message;
            if (selectedItems.Count == 1)
            {
                var item = selectedItems[0];
                message = $"Are you sure you want to delete '{item.Name}'?";
                if (item.IsDirectory)
                {
                    message += "\n\nThis is a directory. All contents will be deleted.";
                }
            }
            else
            {
                var fileCount = selectedItems.Count(i => !i.IsDirectory);
                var folderCount = selectedItems.Count(i => i.IsDirectory);

                message = $"Are you sure you want to delete {selectedItems.Count} items?\n\n";
                if (fileCount > 0) message += $"Files: {fileCount}\n";
                if (folderCount > 0) message += $"Folders: {folderCount}\n";
                if (folderCount > 0) message += "\nAll folder contents will be deleted.";
            }

            var result = MessageBox.Show(
                message,
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // Save tree state before operation
                    SaveTreeExpansionState();

                    StatusText.Text = $"Deleting {selectedItems.Count} item(s)...";

                    int deletedCount = 0;
                    var errors = new List<string>();

                    foreach (var item in selectedItems)
                    {
                        try
                        {
                            await Task.Run(() => fileSystemService.DeleteItem(item.FullPath));
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            // Add more detailed error information
                            errors.Add($"{item.Name} ({item.FullPath}): {ex.Message}");
                        }
                    }

                    // Only refresh current folder, not the entire tree
                    await NavigateToPath(currentPath);

                    // If we deleted folders, update just those nodes in the tree
                    var deletedFolders = selectedItems.Where(i => i.IsDirectory).ToList();
                    if (deletedFolders.Any())
                    {
                        await RefreshParentTreeNode(currentPath);
                    }

                    // Restore tree state
                    await RestoreTreeExpansionState();

                    // Show result
                    if (errors.Count == 0)
                    {
                        StatusText.Text = $"Deleted {deletedCount} item(s)";
                    }
                    else
                    {
                        StatusText.Text = $"Deleted {deletedCount} item(s), {errors.Count} error(s)";
                        if (errors.Count > 0)
                        {
                            string errorMessage = "Some items could not be deleted:\n\n" +
                                                 string.Join("\n", errors.Take(5));
                            if (errors.Count > 5)
                                errorMessage += $"\n... and {errors.Count - 5} more";

                            MessageBox.Show(errorMessage, "Delete Errors",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting items: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Delete operation failed";
                }
            }
        }

        private async Task RefreshParentTreeNode(string path)
        {
            if (string.IsNullOrEmpty(path) || rootFolders.Count == 0)
                return;

            try
            {
                var parentNode = FindNodeByPath(rootFolders[0], path);
                if (parentNode != null)
                {
                    // Reload children for this node
                    await LoadTreeNodeChildren(parentNode);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error refreshing parent node: {ex.Message}");
            }
        }

        private async Task DeleteTreeViewItem(FolderNode folder)
        {
            var message = $"Are you sure you want to delete the folder '{folder.Name}'?\n\n" +
                          "This will delete the folder and all its contents.";

            var result = MessageBox.Show(
                message,
                "Confirm Delete Folder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    StatusText.Text = $"Deleting folder {folder.Name}...";

                    await Task.Run(() => fileSystemService.DeleteItem(folder.FullPath));

                    // Refresh both tree and file list
                    await LoadRootFolders();
                    await RefreshCurrentFolder();

                    StatusText.Text = $"Deleted folder {folder.Name}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting folder: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Delete operation failed";
                }
            }
        }

        // Add rename functionality (optional):
        private async Task RenameSelectedItem()
        {
            if (FileListView.SelectedItems.Count != 1) return;

            if (FileListView.SelectedItem is FileItem item)
            {
                var dialog = new InputDialog("Rename", $"Enter new name for '{item.Name}':", item.Name);

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
                {
                    try
                    {
                        // Save tree state
                        SaveTreeExpansionState();

                        string newName = dialog.ResponseText;
                        string newPath = currentPath == "/"
                            ? "/" + newName
                            : currentPath + "/" + newName;

                        // Use proper escaping for mv command
                        var oldEscaped = EscapeForShell(item.FullPath);
                        var newEscaped = EscapeForShell(newPath);

                        await Task.Run(() =>
                            adbService.ExecuteShellCommand($"mv {oldEscaped} {newEscaped}"));

                        // Only refresh current folder
                        await NavigateToPath(currentPath);

                        // If renamed a folder, update tree
                        if (item.IsDirectory)
                        {
                            await RefreshParentTreeNode(currentPath);
                        }

                        // Restore tree state
                        await RestoreTreeExpansionState();

                        StatusText.Text = $"Renamed to {newName}";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error renaming: {ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private string EscapeForShell(string path)
        {
            // For very complex cases, we can use printf to handle the escaping
            // This handles all special characters including newlines
            var hexPath = BitConverter.ToString(Encoding.UTF8.GetBytes(path)).Replace("-", "\\x");
            return "$'\\x" + hexPath + "'";
        }

        private string currentClipboardTempDir = null;

        private void CleanupTempFiles()
        {
            // Clean up clipboard temp files
            if (!string.IsNullOrEmpty(currentClipboardTempDir))
            {
                try
                {
                    if (Directory.Exists(currentClipboardTempDir))
                    {
                        Directory.Delete(currentClipboardTempDir, true);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        private async void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Implement enhanced copy functionality that works with Windows Explorer
                var selectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
                if (selectedItems.Count > 0)
                {
                    // Always set AdbFiles for internal paste operations
                    Clipboard.SetData("AdbFiles", selectedItems);

                    // For external paste (to Windows Explorer), prepare actual files
                    await PrepareFilesForWindowsClipboard(selectedItems);

                    StatusText.Text = $"Copied {selectedItems.Count} item(s)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during copy operation: {ex.Message}", "Copy Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Copy operation failed";
            }
        }

        private async Task PrepareFilesForWindowsClipboard(List<FileItem> selectedItems)
        {
            // Clean up previous clipboard temp files if any
            if (!string.IsNullOrEmpty(currentClipboardTempDir))
            {
                _ = Task.Run(() => CleanupDragTempFiles(currentClipboardTempDir));
            }

            // Create new temp directory for clipboard files
            currentClipboardTempDir = Path.Combine(Path.GetTempPath(), "AdbExplorer", "Clipboard", Guid.NewGuid().ToString());
            Directory.CreateDirectory(currentClipboardTempDir);

            // Prepare download list
            var downloadList = new List<(string remotePath, string localPath)>();
            foreach (var item in selectedItems)
            {
                if (item.IsDirectory)
                {
                    CollectDirectoryFilesForDragDownload(item.FullPath, Path.Combine(currentClipboardTempDir, item.Name), downloadList);
                }
                else
                {
                    string localPath = Path.Combine(currentClipboardTempDir, item.Name);
                    downloadList.Add((item.FullPath, localPath));
                }
            }

            // For large batches, show progress panel
            if (downloadList.Count >= 4)
            {
                StatusText.Text = $"Preparing {downloadList.Count} files for clipboard...";

                // Show progress panel for the download
                bool success = await fileTransferManager.DownloadFilesAsync(downloadList, true);

                if (success)
                {
                    // Add files to Windows clipboard
                    AddFilesToWindowsClipboard(currentClipboardTempDir);
                    StatusText.Text = $"Copied {selectedItems.Count} item(s) - ready to paste";
                }
                else
                {
                    StatusText.Text = "Copy operation completed with some errors";
                }
            }
            else
            {
                // For small batches, download quickly without progress panel
                StatusText.Text = $"Preparing {selectedItems.Count} item(s) for clipboard...";

                await Task.Run(async () =>
                {
                    foreach (var (remotePath, localPath) in downloadList)
                    {
                        try
                        {
                            // Create directory if needed
                            string localDir = Path.GetDirectoryName(localPath);
                            if (!string.IsNullOrEmpty(localDir) && !Directory.Exists(localDir))
                            {
                                Directory.CreateDirectory(localDir);
                            }

                            fileSystemService.PullFile(remotePath, localPath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to pull {remotePath}: {ex.Message}");
                        }
                    }
                });

                // Add files to Windows clipboard
                AddFilesToWindowsClipboard(currentClipboardTempDir);
                StatusText.Text = $"Copied {selectedItems.Count} item(s) - ready to paste";
            }
        }

        private void AddFilesToWindowsClipboard(string tempDir)
        {
            try
            {
                // Get all files and directories in temp folder
                var files = new System.Collections.Specialized.StringCollection();

                // Add files
                foreach (string file in Directory.GetFiles(tempDir))
                {
                    files.Add(file);
                }

                // Add directories
                foreach (string dir in Directory.GetDirectories(tempDir))
                {
                    files.Add(dir);
                }

                if (files.Count > 0)
                {
                    // Create new data object with both formats
                    var dataObject = new DataObject();

                    // Add file drop list for Windows Explorer
                    dataObject.SetFileDropList(files);

                    // Keep AdbFiles for internal use
                    if (Clipboard.ContainsData("AdbFiles"))
                    {
                        dataObject.SetData("AdbFiles", Clipboard.GetData("AdbFiles"));
                    }

                    // Set the clipboard
                    Clipboard.SetDataObject(dataObject, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting Windows clipboard: {ex.Message}");
            }
        }

        private async void PasteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await PasteItems();
        }

        private async Task<bool> PasteFilesTraditional(List<(string sourcePath, string destPath)> files)
        {
            var errors = new List<string>();

            foreach (var (sourcePath, destPath) in files)
            {
                try
                {
                    StatusText.Text = $"Pasting {Path.GetFileName(sourcePath)}...";

                    var sourceEscaped = EscapeForShell(sourcePath);
                    var destEscaped = EscapeForShell(destPath);

                    // Check if source is directory
                    var sourceFiles = await Task.Run(() => fileSystemService.GetFiles(Path.GetDirectoryName(sourcePath)));
                    var sourceItem = sourceFiles?.FirstOrDefault(f => f.FullPath == sourcePath);
                    bool isDirectory = sourceItem?.IsDirectory ?? false;

                    await Task.Run(() =>
                    {
                        if (isDirectory)
                        {
                            adbService.ExecuteShellCommand($"cp -r {sourceEscaped} {destEscaped}");
                            // Set permissions recursively for directories
                            try
                            {
                                adbService.ExecuteShellCommand($"chmod -R 770 {destEscaped}");
                                adbService.ExecuteShellCommand($"find {destEscaped} -type f -exec chmod 660 {{}} \\;");
                            }
                            catch { /* Ignore permission errors */ }
                        }
                        else
                        {
                            adbService.ExecuteShellCommand($"cp {sourceEscaped} {destEscaped}");
                            // Set permissions to 660 for copied file
                            try
                            {
                                adbService.ExecuteShellCommand($"chmod 660 {destEscaped}");
                            }
                            catch { /* Ignore permission errors */ }
                        }
                    });
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(sourcePath)}: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                string errorMessage = "Some items could not be pasted:\n\n" +
                                     string.Join("\n", errors.Take(5));
                if (errors.Count > 5)
                    errorMessage += $"\n... and {errors.Count - 5} more";

                MessageBox.Show(errorMessage, "Paste Errors",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return errors.Count == 0;
        }

        private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FileListView.SelectedItem is FileItem item)
            {
                var props = new StringBuilder();
                props.AppendLine($"Name: {item.Name}");
                props.AppendLine($"Path: {item.FullPath}");
                props.AppendLine($"Size: {item.Size:N0} bytes");
                props.AppendLine($"Modified: {item.Modified}");
                props.AppendLine($"Permissions: {item.Permissions}");
                props.AppendLine($"Owner: {item.Owner}");
                props.AppendLine($"Group: {item.Group}");
                props.AppendLine($"Accessible: {(item.IsAccessible ? "Yes" : "No")}");

                MessageBox.Show(props.ToString(), "Properties",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Implement tree view context menu if needed
        }

        private async void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem item && item.DataContext is FolderNode node)
            {
                // Check if this is first expansion (has dummy child)
                if (node.Children.Count == 1 && string.IsNullOrEmpty(node.Children[0].Name))
                {
                    await LoadTreeNodeChildren(node);
                }
            }
        }

        public async Task LoadTreeNodeChildren(FolderNode node)
        {
            try
            {
                var files = await Task.Run(() => fileSystemService.GetFiles(node.FullPath));

                await Dispatcher.InvokeAsync(() =>
                {
                    node.Children.Clear();

                    if (files.Count == 0)
                    {
                        return;
                    }

                    foreach (var file in files.Where(f => f.IsDirectory).OrderBy(f => f.Name, new NaturalStringComparer()))
                    {
                        var childNode = new FolderNode
                        {
                            Name = file.Name,
                            FullPath = file.FullPath,
                            IsAccessible = true
                        };

                        childNode.Children.Add(new FolderNode { Name = "" });
                        node.Children.Add(childNode);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error expanding node {node.FullPath}: {ex.Message}");

                await Dispatcher.InvokeAsync(() =>
                {
                    node.Children.Clear();
                    node.IsAccessible = false;
                });
            }
        }

        private void ShowAccessError(string path, string error)
        {
            // Clear the file list
            currentFiles.Clear();

            // Show error info in the list view or a dedicated info panel
            // Option 1: Add an info panel overlay
            Dispatcher.Invoke(() =>
            {
                // You could add a TextBlock overlay to the ListView
                StatusText.Text = $"Access denied: {path}";

                // Or better: Add a nice info panel
                ShowErrorPanel(path, error);
            });
        }

        private void ShowErrorPanel(string path, string error)
        {
            ErrorInfoPanel.Visibility = Visibility.Visible;
            ErrorPathText.Text = $"Path: {path}";
            ErrorMessageText.Text = $"Error: {error}";
        }

        private void HideErrorPanel()
        {
            ErrorInfoPanel.Visibility = Visibility.Collapsed;
        }

        private async void GoBackFromError_Click(object sender, RoutedEventArgs e)
        {
            HideErrorPanel();
            if (navigationHistory.Count > 0)
            {
                string path = navigationHistory.Pop();
                await NavigateToPath(path);
            }
        }

        private async void SyncTreeButton_Click(object sender, RoutedEventArgs e)
        {
            await SyncTreeWithCurrentPath();
        }

        private async Task SyncTreeWithCurrentPath()
        {
            if (string.IsNullOrEmpty(currentPath) || rootFolders.Count == 0)
                return;

            try
            {
                StatusText.Text = "Syncing folder tree...";

                // Set flag to prevent navigation during sync
                isSyncingTree = true;

                // Collapse all nodes first to clean up the view
                CollapseAllTreeNodes();

                // Expand to current path
                await ExpandTreeToPath(currentPath);

                StatusText.Text = "Tree synchronized";

                // Reset flag after sync is complete
                isSyncingTree = false;

                // After a short delay, clear the status
                await Task.Delay(1500);
                StatusText.Text = "Ready";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error syncing tree: {ex.Message}");
                StatusText.Text = "Could not sync tree";
                isSyncingTree = false;  // Make sure to reset flag on error
            }
        }

        private void CollapseAllTreeNodes()
        {
            Dispatcher.Invoke(() =>
            {
                if (FolderTreeView.Items.Count > 0)
                {
                    foreach (var item in FolderTreeView.Items)
                    {
                        var treeViewItem = FolderTreeView.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                        if (treeViewItem != null)
                        {
                            CollapseTreeViewItem(treeViewItem);
                        }
                    }
                }
            });
        }

        private void CollapseTreeViewItem(TreeViewItem item)
        {
            if (item == null) return;

            item.IsExpanded = false;

            foreach (var child in item.Items)
            {
                var childItem = item.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem;
                if (childItem != null)
                {
                    CollapseTreeViewItem(childItem);
                }
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (adbService != null)
            {
                adbService.DevicesChanged -= AdbService_DevicesChanged;
                adbService.StopDeviceTracking();
            }

            allWindows.Remove(this);
            UpdateWindowCount();

            // Only save settings from the last window
            if (allWindows.Count == 0)
            {
                // Save window state
                if (this.WindowState == WindowState.Normal)
                {
                    settings.WindowWidth = (int)this.Width;
                    settings.WindowHeight = (int)this.Height;
                    settings.WindowLeft = this.Left;
                    settings.WindowTop = this.Top;
                }
                settings.Save();
            }

            // Clean up file watcher if it exists
            if (tempFileWatcher != null)
            {
                tempFileWatcher.EnableRaisingEvents = false;
                tempFileWatcher.Dispose();
                tempFileWatcher = null;
            }

            // Clean up temp files only on last window close
            if (allWindows.Count == 0)
            {
                try
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "AdbExplorer");
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch
                {
                    // Ignore - temp files will be cleaned by system
                }
            }

            base.OnClosing(e);
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            ShowHelpDialog();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var aboutDialog = new AboutDialog();
            aboutDialog.Owner = this;
            aboutDialog.ShowDialog();
        }

        private void LoadFavorites()
        {
            // Create an ObservableCollection from settings favorites
            favoritesCollection = new ObservableCollection<FavoriteItem>();
            
            // Always add placeholder as first item
            favoritesCollection.Add(FavoriteItem.CreatePlaceholder());
            
            // Add actual favorites
            if (settings.Favorites != null)
            {
                foreach (var favorite in settings.Favorites)
                {
                    favoritesCollection.Add(favorite);
                }
            }
            
            FavoritesComboBox.ItemsSource = favoritesCollection;
            FavoritesComboBox.SelectedIndex = 0; // Select placeholder by default
            
            // Enable/disable based on actual favorites (not counting placeholder)
            bool hasFavorites = favoritesCollection.Count > 1;
            RemoveFavoriteButton.IsEnabled = hasFavorites;
        }
        
        private void UpdateSelectedFavorite(string path)
        {
            if (favoritesCollection == null) return;
            
            // Find matching favorite
            var matchingFavorite = favoritesCollection.FirstOrDefault(f => !f.IsPlaceholder && f.Path == path);
            
            if (matchingFavorite != null)
            {
                // Select the matching favorite
                FavoritesComboBox.SelectedItem = matchingFavorite;
            }
            else
            {
                // Reset to placeholder if no match
                FavoritesComboBox.SelectedIndex = 0;
            }
        }

        private async void FavoritesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is FavoriteItem favorite && !favorite.IsPlaceholder)
            {
                navigationHistory.Push(currentPath);
                navigationForward.Clear();
                await NavigateToPath(favorite.Path);
                // Keep the selected favorite visible instead of resetting
                // This allows the user to see which favorite they're in and remove it if needed
                
                // Auto-sync the folder tree to show the location
                await SyncTreeWithCurrentPath();
            }
        }

        private void AddFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentPath))
                return;

            // Check if already exists (skip placeholder at index 0)
            if (favoritesCollection != null && favoritesCollection.Skip(1).Any(f => f.Path == currentPath))
            {
                ShowStatusNotification("This folder is already in favorites", true);
                return;
            }

            // Get display name with more context for common folder names
            string displayName = GetSmartDisplayName(currentPath);

            // Add to favorites
            var favorite = new FavoriteItem(currentPath, displayName);
            settings.Favorites.Add(favorite);
            favoritesCollection.Add(favorite);
            settings.Save();

            // Update UI state
            RemoveFavoriteButton.IsEnabled = true;
            
            ShowStatusNotification($"Added '{displayName}' to favorites");
        }

        private void RemoveFavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is FavoriteItem favorite && !favorite.IsPlaceholder)
            {
                // Direct removal without confirmation
                {
                    // Find the matching favorite in settings (may be different object instance)
                    var settingsFavorite = settings.Favorites.FirstOrDefault(f => f.Path == favorite.Path);
                    if (settingsFavorite != null)
                    {
                        settings.Favorites.Remove(settingsFavorite);
                    }
                    
                    favoritesCollection.Remove(favorite);
                    settings.Save();
                    
                    // Reset to placeholder
                    FavoritesComboBox.SelectedIndex = 0;
                    
                    // Update UI state if no favorites left (only placeholder remains)
                    if (favoritesCollection.Count == 1)
                    {
                        RemoveFavoriteButton.IsEnabled = false;
                    }
                    
                    ShowStatusNotification($"Removed '{favorite.DisplayName}' from favorites");
                }
            }
            else if (favoritesCollection.Count > 1)
            {
                ShowStatusNotification("Please select a favorite from the dropdown to remove", true);
            }
        }

        private void TreeAddToFavoritesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FolderTreeView.SelectedItem is FolderNode folder)
            {
                AddPathToFavorites(folder.FullPath, folder.Name);
            }
        }

        private void AddToFavoritesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FileListView.SelectedItem is FileItem item && item.IsDirectory)
            {
                AddPathToFavorites(item.FullPath, item.Name);
            }
        }

        private void AddPathToFavorites(string path, string name)
        {
            // Check if already exists (skip placeholder at index 0)
            if (favoritesCollection != null && favoritesCollection.Skip(1).Any(f => f.Path == path))
            {
                ShowStatusNotification("This folder is already in favorites", true);
                return;
            }

            // Get a smarter display name if the provided one is too generic
            string displayName = name;
            if (IsGenericFolderName(name))
            {
                displayName = GetSmartDisplayName(path);
            }

            // Add to favorites
            var favorite = new FavoriteItem(path, displayName);
            settings.Favorites.Add(favorite);
            
            // Add to ObservableCollection for immediate UI update
            if (favoritesCollection != null)
            {
                favoritesCollection.Add(favorite);
            }
            
            settings.Save();

            // Update UI state
            RemoveFavoriteButton.IsEnabled = true;
            
            ShowStatusNotification($"Added '{displayName}' to favorites");
        }
        
        private bool IsGenericFolderName(string name)
        {
            var genericNames = new[] { "files", "data", "cache", "obb", "temp", "tmp", "download", "downloads", "documents", "pictures", "music", "videos" };
            return genericNames.Contains(name.ToLower());
        }
        
        private string GetSmartDisplayName(string path)
        {
            if (path == "/") return "Root (/)";
            
            string[] parts = path.TrimEnd('/').Split('/');
            string lastPart = parts[parts.Length - 1];
            
            // For generic folder names, include parent folder(s) for context
            if (IsGenericFolderName(lastPart) && parts.Length > 1)
            {
                // For paths like /sdcard/Android/data/com.hyperionics.avar/files
                // Try to find the most relevant parent (usually app package name)
                if (path.Contains("/Android/data/") || path.Contains("/Android/obb/"))
                {
                    // Extract app package name
                    for (int i = parts.Length - 2; i >= 0; i--)
                    {
                        if (parts[i].Contains(".") && parts[i].Length > 5) // Likely a package name
                        {
                            return $"{parts[i]}/{lastPart}";
                        }
                    }
                }
                
                // For other cases, include immediate parent
                return $"{parts[parts.Length - 2]}/{lastPart}";
            }
            
            return lastPart;
        }

        private void FavoritesComboBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Right-click support for context menu
            if (FavoritesComboBox.SelectedItem is FavoriteItem favorite && !favorite.IsPlaceholder)
            {
                // Context menu will show automatically
            }
        }
        
        private void EditFavoriteName_Click(object sender, RoutedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is FavoriteItem favorite && !favorite.IsPlaceholder)
            {
                var inputDialog = new InputDialog("Edit Favorite Name", 
                    "Enter a new name for this favorite:", 
                    favorite.DisplayName);
                inputDialog.Owner = this;
                
                if (inputDialog.ShowDialog() == true)
                {
                    string newName = inputDialog.ResponseText.Trim();
                    if (!string.IsNullOrEmpty(newName))
                    {
                        // Update the display name
                        favorite.DisplayName = newName;
                        
                        // Find and update in settings
                        var settingsFavorite = settings.Favorites.FirstOrDefault(f => f.Path == favorite.Path);
                        if (settingsFavorite != null)
                        {
                            settingsFavorite.DisplayName = newName;
                            settings.Save();
                        }
                        
                        // Refresh the ComboBox display
                        int currentIndex = FavoritesComboBox.SelectedIndex;
                        FavoritesComboBox.Items.Refresh();
                        FavoritesComboBox.SelectedIndex = currentIndex;
                        
                        ShowStatusNotification($"Favorite renamed to '{newName}'");
                    }
                }
            }
        }
        
        private void RemoveFavoriteContext_Click(object sender, RoutedEventArgs e)
        {
            RemoveFavoriteButton_Click(sender, e);
        }

        private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await RenameSelectedItem();
        }
        
        private async void TreeRenameMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FolderTreeView.SelectedItem is FolderNode folder && folder.FullPath != "/")
            {
                var dialog = new InputDialog("Rename Folder", $"Enter new name for '{folder.Name}':", folder.Name);
                dialog.Owner = this;
                
                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
                {
                    try
                    {
                        string newName = dialog.ResponseText;
                        string parentPath = Path.GetDirectoryName(folder.FullPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "/";
                        string newPath = parentPath == "/" ? "/" + newName : parentPath + "/" + newName;
                        
                        // Use proper escaping for mv command
                        var oldEscaped = EscapeForShell(folder.FullPath);
                        var newEscaped = EscapeForShell(newPath);
                        
                        await Task.Run(() =>
                            adbService.ExecuteShellCommand($"mv {oldEscaped} {newEscaped}"));
                        
                        // Refresh the parent node in the tree
                        await RefreshParentTreeNode(parentPath);
                        
                        // If we were in the renamed folder, navigate to parent
                        if (currentPath.StartsWith(folder.FullPath))
                        {
                            await NavigateToPath(parentPath);
                        }
                        
                        StatusText.Text = $"Renamed folder to {newName}";
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error renaming folder: {ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void ShowHelpDialog()
        {
            var helpDialog = new HelpDialog();
            helpDialog.Owner = this;
            helpDialog.ShowDialog();
        }

        private async void CopySelectedItems()
        {
            var selectedItems = FileListView.SelectedItems.Cast<FileItem>().ToList();
            if (selectedItems.Count > 0)
            {
                // Always set AdbFiles for internal paste operations
                Clipboard.SetData("AdbFiles", selectedItems);

                // For external paste (to Windows Explorer), prepare actual files
                await PrepareFilesForWindowsClipboard(selectedItems);

                StatusText.Text = $"Copied {selectedItems.Count} item(s)";
            }
        }

        // Add helper method for Paste operation
        private async Task PasteItems()
        {
            try
            {
                await PasteFromClipboardAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during paste operation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Paste operation failed";
            }
        }

        private async Task PasteFromClipboardAsync()
        {
            if (TryGetClipboardAdbFiles(out var adbFiles))
            {
                await PasteAdbFilesAsync(adbFiles);
                return;
            }

            if (TryGetClipboardFileDropList(out var files))
            {
                await HandleExternalFileDrop(files, currentPath);
                return;
            }

            StatusText.Text = "No files to paste";
        }

        private bool TryGetClipboardAdbFiles(out List<FileItem> items)
        {
            items = new List<FileItem>();

            try
            {
                if (!Clipboard.ContainsData("AdbFiles"))
                {
                    return false;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                StatusText.Text = "Clipboard data is invalid or corrupted";
                return false;
            }

            try
            {
                items = Clipboard.GetData("AdbFiles") as List<FileItem> ?? new List<FileItem>();
                if (items.Count == 0)
                {
                    return false;
                }

                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                StatusText.Text = "Failed to read clipboard data - data may be corrupted";
                return false;
            }
        }

        private bool TryGetClipboardFileDropList(out string[] files)
        {
            files = Array.Empty<string>();

            try
            {
                if (!Clipboard.ContainsFileDropList())
                {
                    return false;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return false;
            }

            try
            {
                var fileDropList = Clipboard.GetFileDropList();
                if (fileDropList == null || fileDropList.Count == 0)
                {
                    return false;
                }

                files = new string[fileDropList.Count];
                fileDropList.CopyTo(files, 0);
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to read Windows clipboard files: {ex.Message}";
                return false;
            }
        }

        private async Task PasteAdbFilesAsync(List<FileItem> items)
        {
            var existingNames = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var targetFiles = await Task.Run(() => fileSystemService.GetFiles(currentPath));
                foreach (var targetFile in targetFiles)
                {
                    existingNames.Add(targetFile.Name);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error checking target directory: {ex.Message}";
                return;
            }

            var conflicts = items
                .Where(item => existingNames.Contains(item.Name))
                .Select(item => item.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (conflicts.Count > 0)
            {
                string fileList = conflicts.Count <= 5
                    ? string.Join("\n", conflicts)
                    : string.Join("\n", conflicts.Take(5)) + $"\n... and {conflicts.Count - 5} more";

                string message = conflicts.Count == 1
                    ? $"The following item already exists:\n\n{fileList}\n\nDo you want to overwrite it?"
                    : $"The following items already exist:\n\n{fileList}\n\nDo you want to overwrite them?";

                var result = MessageBox.Show(message, "Paste Files",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Paste cancelled";
                    return;
                }
            }

            var copyList = new List<(string sourcePath, string destPath)>();

            foreach (var file in items)
            {
                string destPath = currentPath == "/"
                    ? "/" + file.Name
                    : currentPath + "/" + file.Name;

                copyList.Add((file.FullPath, destPath));
            }

            bool success;
            if (copyList.Count >= 4)
            {
                success = await fileTransferManager.CopyFilesOnDeviceAsync(copyList, true);
            }
            else
            {
                success = await PasteFilesTraditional(copyList);
            }

            await RefreshCurrentFolder();

            if (success)
            {
                StatusText.Text = $"Pasted {copyList.Count} item(s)";
            }
            else
            {
                StatusText.Text = "Paste completed with some errors";
            }
        }

        private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FileListView.SelectedItem is FileItem item)
            {
                if (item.IsDirectory)
                {
                    navigationHistory.Push(currentPath);
                    navigationForward.Clear();
                    _ = NavigateToPath(item.FullPath);
                }
                else
                {
                    _ = OpenFileInWindows(item);
                }
            }
        }

        private void TreeDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FolderTreeView.SelectedItem is FolderNode folder)
            {
                _ = DeleteTreeViewItem(folder);
            }
        }

        private void TreePropertiesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (FolderTreeView.SelectedItem is FolderNode folder)
            {
                var props = new StringBuilder();
                props.AppendLine($"Name: {folder.Name}");
                props.AppendLine($"Path: {folder.FullPath}");
                props.AppendLine($"Accessible: {(folder.IsAccessible ? "Yes" : "No")}");

                MessageBox.Show(props.ToString(), "Folder Properties",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}

public class RelayCommand : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool>? canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object? parameter)
        {
            return canExecute?.Invoke() ?? true;
        }

        public void Execute(object? parameter)
        {
            execute();
        }

}
