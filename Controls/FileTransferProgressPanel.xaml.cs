using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using AdbExplorer.Models;

namespace AdbExplorer.Controls
{
    /// <summary>
    /// Interaction logic for FileTransferProgressPanel.xaml
    /// </summary>
    public partial class FileTransferProgressPanel : UserControl
    {
        private FileTransferQueue _transferQueue;
        private DispatcherTimer _updateTimer;
        private Action<FileTransferOperation> _skipCallback;
        private Action _stopCallback;
        private Action _pauseResumeCallback;
        private bool _isPaused;

        public FileTransferProgressPanel()
        {
            InitializeComponent();

            // Set up the update timer for refreshing speed and ETA
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _updateTimer.Tick += UpdateTimer_Tick;

            // Wire up button events
            SkipButton.Click += SkipButton_Click;
            StopButton.Click += StopButton_Click;
            PauseResumeButton.Click += PauseResumeButton_Click;
            CloseButton.Click += CloseButton_Click;

            // Set default visibility
            IsEnabled = true;
        }

        public FileTransferQueue TransferQueue
        {
            get => _transferQueue;
            set
            {
                if (_transferQueue != null)
                {
                    _transferQueue.PropertyChanged -= TransferQueue_PropertyChanged;
                }

                _transferQueue = value;

                if (_transferQueue != null)
                {
                    _transferQueue.PropertyChanged += TransferQueue_PropertyChanged;
                    FileListGrid.ItemsSource = _transferQueue.Operations;
                    UpdateUI();
                    _updateTimer.Start();
                }
                else
                {
                    FileListGrid.ItemsSource = null;
                    _updateTimer.Stop();
                }
            }
        }

        public void SetCallbacks(Action<FileTransferOperation> skipCallback,
                                 Action stopCallback,
                                 Action pauseResumeCallback = null)
        {
            _skipCallback = skipCallback;
            _stopCallback = stopCallback;
            _pauseResumeCallback = pauseResumeCallback;

            // Hide pause button if no callback provided
            PauseResumeButton.Visibility = pauseResumeCallback != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void TransferQueue_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateUI()));
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            if (_transferQueue != null && _transferQueue.IsActive)
            {
                UpdateUI();
            }
        }

        private void UpdateUI()
        {
            if (_transferQueue == null) return;

            // Update header
            OperationTitle.Text = _transferQueue.DirectionText;
            OverallPercentage.Text = _transferQueue.FormattedProgress;
            OverallProgressBar.Value = _transferQueue.OverallProgress;

            // Update statistics
            FileCountText.Text = _transferQueue.FormattedFileCount;
            SizeProgressText.Text = _transferQueue.FormattedSizeProgress;
            SpeedText.Text = string.IsNullOrEmpty(_transferQueue.FormattedSpeed)
                ? ""
                : $"Speed: {_transferQueue.FormattedSpeed}";
            ETAText.Text = string.IsNullOrEmpty(_transferQueue.FormattedETA)
                ? ""
                : $"ETA: {_transferQueue.FormattedETA}";

            // Update current file info
            var currentOp = _transferQueue.CurrentOperation;
            if (currentOp != null)
            {
                CurrentFileName.Text = currentOp.FileName;
                CurrentFileProgress.Text = currentOp.FormattedProgress;
                CurrentFileProgressBar.Value = currentOp.Progress;
                SourcePathText.Text = $"From: {currentOp.SourcePath}";
                DestinationPathText.Text = $"To: {currentOp.DestinationPath}";
            }
            else
            {
                CurrentFileName.Text = "";
                CurrentFileProgress.Text = "";
                CurrentFileProgressBar.Value = 0;
                SourcePathText.Text = "";
                DestinationPathText.Text = "";
            }

            // Update status message
            UpdateStatusMessage();

            // Show error column if there are errors
            var errorColumn = FileListGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == "Error");
            if (errorColumn != null)
            {
                errorColumn.Visibility = _transferQueue.HasErrors ? Visibility.Visible : Visibility.Collapsed;
            }

            // Update button states
            UpdateButtonStates();

            // Auto-scroll to current operation
            if (currentOp != null && FileListGrid.Items.Contains(currentOp))
            {
                FileListGrid.ScrollIntoView(currentOp);
                FileListGrid.SelectedItem = currentOp;
            }
        }

        private string _completionMessage = null;

        public void SetCompletionMessage(string message)
        {
            _completionMessage = message;
        }

        private void UpdateStatusMessage()
        {
            if (_transferQueue == null)
            {
                StatusMessage.Text = "";
                return;
            }

            if (_transferQueue.IsCompleted)
            {
                if (_transferQueue.HasErrors)
                {
                    StatusMessage.Text = $"Completed with {_transferQueue.ErrorCount} error(s)";
                    StatusMessage.Foreground = System.Windows.Media.Brushes.Red;
                }
                else if (!string.IsNullOrEmpty(_completionMessage))
                {
                    // Show custom completion message prominently
                    StatusMessage.Text = _completionMessage;
                    StatusMessage.Foreground = System.Windows.Media.Brushes.Green;
                    StatusMessage.FontWeight = System.Windows.FontWeights.Bold;
                    StatusMessage.FontSize = 14;
                }
                else
                {
                    StatusMessage.Text = "All transfers completed successfully";
                    StatusMessage.Foreground = System.Windows.Media.Brushes.Green;
                }
            }
            else if (_isPaused)
            {
                StatusMessage.Text = "Paused";
                StatusMessage.Foreground = System.Windows.Media.Brushes.Orange;
            }
            else if (_transferQueue.IsActive)
            {
                StatusMessage.Text = "";
                StatusMessage.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        private void UpdateButtonStates()
        {
            if (_transferQueue == null) return;

            bool isActive = _transferQueue.IsActive && !_transferQueue.IsCompleted;

            SkipButton.IsEnabled = isActive && _transferQueue.CurrentOperation != null;
            StopButton.IsEnabled = isActive;

            if (PauseResumeButton.Visibility == Visibility.Visible)
            {
                PauseResumeButton.Content = _isPaused ? "Resume" : "Pause";
                PauseResumeButton.IsEnabled = isActive || _isPaused;
            }

            // Close button is always enabled so user can close panel at any time
            CloseButton.IsEnabled = true;

            // Check on-finish action
            HandleOnFinishAction();
        }

        private void HandleOnFinishAction()
        {
            if (_transferQueue != null && _transferQueue.IsCompleted)
            {
                var selectedItem = OnFinishComboBox.SelectedItem as ComboBoxItem;
                if (selectedItem != null)
                {
                    string action = selectedItem.Content.ToString();

                    if (action == "Close panel" ||
                        (action == "Close if no errors" && !_transferQueue.HasErrors))
                    {
                        // Fire event to close the panel
                        OnCloseRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            if (_skipCallback != null && _transferQueue?.CurrentOperation != null)
            {
                _skipCallback(_transferQueue.CurrentOperation);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Are you sure you want to stop all transfers?",
                               "Stop Transfers",
                               MessageBoxButton.YesNo,
                               MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _stopCallback?.Invoke();
                _transferQueue?.Cancel();
            }
        }

        private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            _pauseResumeCallback?.Invoke();

            if (_isPaused)
            {
                _transferQueue?.Pause();
            }
            else
            {
                _transferQueue?.Resume();
            }

            UpdateButtonStates();
            UpdateStatusMessage();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Fire event to close the panel
            OnCloseRequested?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler OnCloseRequested;
    }

    /// <summary>
    /// Converter for formatting file sizes
    /// </summary>
    public class FileSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is long bytes)
            {
                return FileTransferOperation.FormatSize(bytes);
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}