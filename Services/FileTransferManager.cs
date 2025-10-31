using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdbExplorer.Controls;
using AdbExplorer.Models;

namespace AdbExplorer.Services
{
    /// <summary>
    /// Manages file transfer operations with progress tracking
    /// </summary>
    public class FileTransferManager
    {
        private readonly AdbService _adbService;
        private readonly FileSystemService _fileSystemService;
        private readonly Window _parentWindow;
        private FileTransferProgressPanel _progressPanel;
        private Grid _overlayContainer;
        private FileTransferQueue _currentQueue;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isSkipRequested;
        private const int BATCH_THRESHOLD = 4; // Show panel for 4+ files

        public FileTransferManager(AdbService adbService, FileSystemService fileSystemService, Window parentWindow)
        {
            _adbService = adbService;
            _fileSystemService = fileSystemService;
            _parentWindow = parentWindow;
        }

        /// <summary>
        /// Upload multiple files from local to device
        /// </summary>
        public async Task<bool> UploadFilesAsync(List<(string localPath, string remotePath)> files, bool showProgress = true)
        {
            if (files == null || files.Count == 0) return true;

            // For small batches, use simple transfer without progress panel
            if (files.Count < BATCH_THRESHOLD || !showProgress)
            {
                return await UploadFilesSimpleAsync(files);
            }

            // Create transfer queue
            var queue = new FileTransferQueue(TransferDirection.Upload);

            // Calculate total size and create operations
            foreach (var (localPath, remotePath) in files)
            {
                var operation = CreateUploadOperation(localPath, remotePath);
                if (operation != null)
                {
                    queue.AddOperation(operation);
                }
            }

            // Execute with progress panel
            return await ExecuteTransferQueueAsync(queue);
        }

        /// <summary>
        /// Download multiple files from device to local
        /// </summary>
        public async Task<bool> DownloadFilesAsync(List<(string remotePath, string localPath)> files, bool showProgress = true)
        {
            if (files == null || files.Count == 0) return true;

            // For small batches, use simple transfer without progress panel
            if (files.Count < BATCH_THRESHOLD || !showProgress)
            {
                return await DownloadFilesSimpleAsync(files);
            }

            // Create transfer queue
            var queue = new FileTransferQueue(TransferDirection.Download);

            // Create operations (note: getting file size from device requires additional calls)
            foreach (var (remotePath, localPath) in files)
            {
                var operation = await CreateDownloadOperationAsync(remotePath, localPath);
                if (operation != null)
                {
                    queue.AddOperation(operation);
                }
            }

            // Execute with progress panel
            return await ExecuteTransferQueueAsync(queue);
        }

        /// <summary>
        /// Copy files within the device
        /// </summary>
        public async Task<bool> CopyFilesOnDeviceAsync(List<(string sourcePath, string destPath)> files, bool showProgress = true)
        {
            if (files == null || files.Count == 0) return true;

            // For small batches, use simple transfer without progress panel
            if (files.Count < BATCH_THRESHOLD || !showProgress)
            {
                return await CopyFilesOnDeviceSimpleAsync(files);
            }

            // Create transfer queue
            var queue = new FileTransferQueue(TransferDirection.DeviceCopy);

            // Create operations
            foreach (var (sourcePath, destPath) in files)
            {
                var operation = await CreateDeviceCopyOperationAsync(sourcePath, destPath);
                if (operation != null)
                {
                    queue.AddOperation(operation);
                }
            }

            // Execute with progress panel
            return await ExecuteTransferQueueAsync(queue);
        }

        private FileTransferOperation CreateUploadOperation(string localPath, string remotePath)
        {
            try
            {
                var fileInfo = new FileInfo(localPath);
                if (!fileInfo.Exists)
                {
                    // Handle directories
                    var dirInfo = new DirectoryInfo(localPath);
                    if (dirInfo.Exists)
                    {
                        return new FileTransferOperation
                        {
                            SourcePath = localPath,
                            DestinationPath = remotePath,
                            TotalSize = GetDirectorySize(dirInfo),
                            Status = TransferStatus.Pending,
                            IsDirectory = true
                        };
                    }
                    return null;
                }

                return new FileTransferOperation
                {
                    SourcePath = localPath,
                    DestinationPath = remotePath,
                    TotalSize = fileInfo.Length,
                    Status = TransferStatus.Pending,
                    IsDirectory = false
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<FileTransferOperation> CreateDownloadOperationAsync(string remotePath, string localPath)
        {
            try
            {
                // Get file info from device
                var fileItems = await Task.Run(() => _fileSystemService.GetFiles(Path.GetDirectoryName(remotePath)));
                var fileName = Path.GetFileName(remotePath);
                var fileItem = fileItems?.FirstOrDefault(f => f.Name == fileName);

                return new FileTransferOperation
                {
                    SourcePath = remotePath,
                    DestinationPath = localPath,
                    TotalSize = fileItem?.Size ?? 0,
                    Status = TransferStatus.Pending,
                    IsDirectory = fileItem?.IsDirectory ?? false
                };
            }
            catch
            {
                // If we can't get size, create with zero size (will show as calculating)
                return new FileTransferOperation
                {
                    SourcePath = remotePath,
                    DestinationPath = localPath,
                    TotalSize = 0,
                    Status = TransferStatus.Pending,
                    IsDirectory = false
                };
            }
        }

        private async Task<FileTransferOperation> CreateDeviceCopyOperationAsync(string sourcePath, string destPath)
        {
            try
            {
                // Get file info from device
                var fileItems = await Task.Run(() => _fileSystemService.GetFiles(Path.GetDirectoryName(sourcePath)));
                var fileName = Path.GetFileName(sourcePath);
                var fileItem = fileItems?.FirstOrDefault(f => f.Name == fileName);

                return new FileTransferOperation
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    TotalSize = fileItem?.Size ?? 0,
                    Status = TransferStatus.Pending,
                    IsDirectory = fileItem?.IsDirectory ?? false
                };
            }
            catch
            {
                return new FileTransferOperation
                {
                    SourcePath = sourcePath,
                    DestinationPath = destPath,
                    TotalSize = 0,
                    Status = TransferStatus.Pending,
                    IsDirectory = false
                };
            }
        }

        private long GetDirectorySize(DirectoryInfo directory)
        {
            try
            {
                long size = 0;
                var files = directory.GetFiles("*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    size += file.Length;
                }
                return size;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<bool> ExecuteTransferQueueAsync(FileTransferQueue queue)
        {
            _currentQueue = queue;
            _cancellationTokenSource = new CancellationTokenSource();

            // Show progress panel
            ShowProgressPanel(queue);

            try
            {
                queue.IsActive = true;
                bool allSuccess = true;

                foreach (var operation in queue.Operations)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (_isSkipRequested)
                    {
                        queue.MarkOperationSkipped(operation);
                        _isSkipRequested = false;
                        continue;
                    }

                    queue.CurrentOperation = operation;
                    operation.Status = TransferStatus.InProgress;

                    bool success = false;
                    try
                    {
                        // Execute the transfer based on direction
                        switch (queue.Direction)
                        {
                            case TransferDirection.Upload:
                                success = await UploadFileWithProgressAsync(operation, queue);
                                break;

                            case TransferDirection.Download:
                                success = await DownloadFileWithProgressAsync(operation, queue);
                                break;

                            case TransferDirection.DeviceCopy:
                                success = await CopyFileOnDeviceWithProgressAsync(operation, queue);
                                break;
                        }

                        if (success)
                        {
                            queue.MarkOperationCompleted(operation);
                        }
                        else
                        {
                            queue.MarkOperationError(operation, "Transfer failed");
                            allSuccess = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        queue.MarkOperationError(operation, ex.Message);
                        allSuccess = false;
                    }
                }

                queue.IsActive = false;
                return allSuccess;
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _currentQueue = null;
            }
        }

        private async Task<bool> UploadFileWithProgressAsync(FileTransferOperation operation, FileTransferQueue queue)
        {
            // Create progress callback
            var progressCallback = new Progress<long>(bytes =>
            {
                _parentWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    queue.UpdateProgress(operation, bytes);
                }));
            });

            // Call enhanced PushFile with progress
            return await Task.Run(() =>
                _adbService.PushFileWithProgress(operation.SourcePath, operation.DestinationPath, progressCallback, _cancellationTokenSource.Token));
        }

        private async Task<bool> DownloadFileWithProgressAsync(FileTransferOperation operation, FileTransferQueue queue)
        {
            // Create progress callback
            var progressCallback = new Progress<long>(bytes =>
            {
                _parentWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    queue.UpdateProgress(operation, bytes);
                }));
            });

            // Call enhanced PullFile with progress
            return await Task.Run(() =>
                _adbService.PullFileWithProgress(operation.SourcePath, operation.DestinationPath, progressCallback, _cancellationTokenSource.Token));
        }

        private async Task<bool> CopyFileOnDeviceWithProgressAsync(FileTransferOperation operation, FileTransferQueue queue)
        {
            // Device-to-device copy doesn't have real progress tracking via ADB
            // We'll simulate progress or use shell commands
            return await Task.Run(() =>
            {
                string command = operation.IsDirectory
                    ? $"cp -r \"{operation.SourcePath}\" \"{operation.DestinationPath}\""
                    : $"cp \"{operation.SourcePath}\" \"{operation.DestinationPath}\"";

                var result = _adbService.ExecuteShellCommand(command);

                // Simulate progress completion
                _parentWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    queue.UpdateProgress(operation, operation.TotalSize);
                }));

                return string.IsNullOrEmpty(result) || !result.Contains("error", StringComparison.OrdinalIgnoreCase);
            });
        }

        private async Task<bool> UploadFilesSimpleAsync(List<(string localPath, string remotePath)> files)
        {
            bool allSuccess = true;
            foreach (var (localPath, remotePath) in files)
            {
                try
                {
                    await Task.Run(() => _adbService.PushFile(localPath, remotePath));
                }
                catch
                {
                    allSuccess = false;
                }
            }
            return allSuccess;
        }

        private async Task<bool> DownloadFilesSimpleAsync(List<(string remotePath, string localPath)> files)
        {
            bool allSuccess = true;
            foreach (var (remotePath, localPath) in files)
            {
                bool success = await Task.Run(() => _adbService.PullFile(remotePath, localPath));
                if (!success) allSuccess = false;
            }
            return allSuccess;
        }

        private async Task<bool> CopyFilesOnDeviceSimpleAsync(List<(string sourcePath, string destPath)> files)
        {
            bool allSuccess = true;
            foreach (var (sourcePath, destPath) in files)
            {
                string command = $"cp \"{sourcePath}\" \"{destPath}\"";
                var result = await Task.Run(() => _adbService.ExecuteShellCommand(command));
                if (!string.IsNullOrEmpty(result) && result.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    allSuccess = false;
                }
            }
            return allSuccess;
        }

        private void ShowProgressPanel(FileTransferQueue queue)
        {
            _parentWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Find the main grid in the window
                var mainGrid = _parentWindow.Content as Grid;
                if (mainGrid == null) return;

                // If a panel already exists, reuse it with the new queue
                if (_progressPanel != null && _overlayContainer != null)
                {
                    // Update the queue for the existing panel
                    _progressPanel.TransferQueue = queue;

                    // Update callbacks with new cancellation token
                    _progressPanel.SetCallbacks(
                        skipCallback: (op) => _isSkipRequested = true,
                        stopCallback: () => _cancellationTokenSource?.Cancel(),
                        pauseResumeCallback: null
                    );
                    return;
                }

                // Create overlay container
                _overlayContainer = new Grid();
                // Set spans only if greater than 1 (avoid setting to 0 which causes exception)
                int rowCount = Math.Max(1, mainGrid.RowDefinitions.Count);
                int columnCount = Math.Max(1, mainGrid.ColumnDefinitions.Count);
                Grid.SetRowSpan(_overlayContainer, rowCount);
                Grid.SetColumnSpan(_overlayContainer, columnCount);

                // Create progress panel
                _progressPanel = new FileTransferProgressPanel
                {
                    TransferQueue = queue,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MaxHeight = 400,
                    Margin = new Thickness(10, 0, 10, 10)
                };

                // Set up callbacks
                _progressPanel.SetCallbacks(
                    skipCallback: (op) => _isSkipRequested = true,
                    stopCallback: () => _cancellationTokenSource?.Cancel(),
                    pauseResumeCallback: null // Pause not implemented in basic version
                );

                // Handle close request
                _progressPanel.OnCloseRequested += (s, e) => HideProgressPanel();

                // Add to overlay
                _overlayContainer.Children.Add(_progressPanel);

                // Add overlay to main grid
                mainGrid.Children.Add(_overlayContainer);
            }));
        }

        private void HideProgressPanel()
        {
            _parentWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_overlayContainer != null && _overlayContainer.Parent is Grid mainGrid)
                {
                    mainGrid.Children.Remove(_overlayContainer);
                    _overlayContainer = null;
                    _progressPanel = null;

                    // Restore focus to FileListView so keyboard shortcuts continue to work
                    var fileListView = FindFileListView(mainGrid);
                    if (fileListView != null)
                    {
                        fileListView.Focus();
                    }
                }
            }));
        }

        private System.Windows.Controls.ListView FindFileListView(Grid grid)
        {
            // Recursively search for the FileListView control
            foreach (var child in grid.Children)
            {
                if (child is System.Windows.Controls.ListView listView && listView.Name == "FileListView")
                {
                    return listView;
                }
                else if (child is Grid childGrid)
                {
                    var result = FindFileListView(childGrid);
                    if (result != null) return result;
                }
                else if (child is Panel panel)
                {
                    var result = FindInPanel(panel);
                    if (result != null) return result;
                }
            }
            return null;
        }

        private System.Windows.Controls.ListView FindInPanel(Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is System.Windows.Controls.ListView listView && listView.Name == "FileListView")
                {
                    return listView;
                }
                else if (child is Grid childGrid)
                {
                    var result = FindFileListView(childGrid);
                    if (result != null) return result;
                }
                else if (child is Panel childPanel)
                {
                    var result = FindInPanel(childPanel);
                    if (result != null) return result;
                }
            }
            return null;
        }

        public void CancelCurrentTransfer()
        {
            _cancellationTokenSource?.Cancel();
        }

        public void SkipCurrentFile()
        {
            _isSkipRequested = true;
        }
    }
}