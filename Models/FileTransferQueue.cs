using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AdbExplorer.Models
{
    /// <summary>
    /// Manages a queue of file transfer operations with overall progress tracking
    /// </summary>
    public class FileTransferQueue : INotifyPropertyChanged
    {
        private readonly ObservableCollection<FileTransferOperation> _operations;
        private long _totalBytes;
        private long _transferredBytes;
        private int _completedCount;
        private int _errorCount;
        private DateTime _startTime;
        private DateTime? _endTime;
        private double _currentSpeed;
        private TransferDirection _direction;
        private bool _isActive;
        private CancellationTokenSource _cancellationTokenSource;
        private FileTransferOperation _currentOperation;

        public FileTransferQueue(TransferDirection direction)
        {
            _operations = new ObservableCollection<FileTransferOperation>();
            _direction = direction;
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public ObservableCollection<FileTransferOperation> Operations => _operations;

        public FileTransferOperation CurrentOperation
        {
            get => _currentOperation;
            set
            {
                _currentOperation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentFileName));
            }
        }

        public string CurrentFileName => CurrentOperation?.FileName ?? "";

        public long TotalBytes
        {
            get => _totalBytes;
            set
            {
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedTotalSize));
                OnPropertyChanged(nameof(OverallProgress));
            }
        }

        public long TransferredBytes
        {
            get => _transferredBytes;
            set
            {
                _transferredBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedTransferredSize));
                OnPropertyChanged(nameof(OverallProgress));
                OnPropertyChanged(nameof(FormattedProgress));
            }
        }

        public int TotalCount => _operations.Count;

        public int CompletedCount
        {
            get => _completedCount;
            set
            {
                _completedCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedFileCount));
                OnPropertyChanged(nameof(RemainingCount));
            }
        }

        public int ErrorCount
        {
            get => _errorCount;
            set
            {
                _errorCount = value;
                OnPropertyChanged();
            }
        }

        public int RemainingCount => TotalCount - CompletedCount - ErrorCount;

        public double OverallProgress
        {
            get
            {
                if (TotalBytes == 0) return 0;
                return (double)TransferredBytes / TotalBytes * 100;
            }
        }

        public string FormattedProgress => $"{OverallProgress:F1}%";

        public string FormattedFileCount => $"Files: {CompletedCount} / {TotalCount}";

        public string FormattedTotalSize => FileTransferOperation.FormatSize(TotalBytes);

        public string FormattedTransferredSize => FileTransferOperation.FormatSize(TransferredBytes);

        public string FormattedSizeProgress => $"Size: {FormattedTransferredSize} / {FormattedTotalSize}";

        public double CurrentSpeed
        {
            get => _currentSpeed;
            set
            {
                _currentSpeed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedSpeed));
                OnPropertyChanged(nameof(EstimatedTimeRemaining));
                OnPropertyChanged(nameof(FormattedETA));
            }
        }

        public string FormattedSpeed
        {
            get
            {
                if (CurrentSpeed == 0 || !IsActive) return "";
                return FileTransferOperation.FormatSpeed(CurrentSpeed);
            }
        }

        public TimeSpan EstimatedTimeRemaining
        {
            get
            {
                if (CurrentSpeed <= 0 || !IsActive) return TimeSpan.Zero;
                long remainingBytes = TotalBytes - TransferredBytes;
                double secondsRemaining = remainingBytes / CurrentSpeed;
                return TimeSpan.FromSeconds(secondsRemaining);
            }
        }

        public string FormattedETA
        {
            get
            {
                var eta = EstimatedTimeRemaining;
                if (eta == TimeSpan.Zero || !IsActive) return "";

                if (eta.TotalDays >= 1)
                    return $"{(int)eta.TotalDays}d {eta.Hours}h {eta.Minutes}m";
                if (eta.TotalHours >= 1)
                    return $"{(int)eta.TotalHours}h {eta.Minutes}m {eta.Seconds}s";
                if (eta.TotalMinutes >= 1)
                    return $"{(int)eta.TotalMinutes}m {eta.Seconds}s";
                return $"{eta.Seconds}s";
            }
        }

        public TransferDirection Direction
        {
            get => _direction;
            set
            {
                _direction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DirectionText));
            }
        }

        public string DirectionText
        {
            get
            {
                return Direction switch
                {
                    TransferDirection.Upload => "Uploading",
                    TransferDirection.Download => "Downloading",
                    TransferDirection.DeviceCopy => "Copying",
                    _ => "Transferring"
                };
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;
                OnPropertyChanged();

                if (value && _startTime == default)
                {
                    _startTime = DateTime.Now;
                }
                else if (!value && _startTime != default)
                {
                    _endTime = DateTime.Now;
                }
            }
        }

        public DateTime StartTime
        {
            get => _startTime;
            set { _startTime = value; OnPropertyChanged(); }
        }

        public DateTime? EndTime
        {
            get => _endTime;
            set { _endTime = value; OnPropertyChanged(); }
        }

        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public void AddOperation(FileTransferOperation operation)
        {
            _operations.Add(operation);
            TotalBytes += operation.TotalSize;
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(FormattedFileCount));
        }

        public void UpdateProgress(FileTransferOperation operation, long bytesTransferred)
        {
            // Update the specific operation
            long previousBytes = operation.BytesTransferred;
            operation.BytesTransferred = bytesTransferred;

            // Update total progress
            long deltaBytes = bytesTransferred - previousBytes;
            TransferredBytes += deltaBytes;

            // Calculate speed based on time elapsed
            if (_startTime != default)
            {
                var elapsed = DateTime.Now - _startTime;
                if (elapsed.TotalSeconds > 0)
                {
                    CurrentSpeed = TransferredBytes / elapsed.TotalSeconds;
                }
            }
        }

        public void MarkOperationCompleted(FileTransferOperation operation)
        {
            operation.Status = TransferStatus.Completed;
            CompletedCount++;

            // Ensure we've counted all bytes for this operation
            if (operation.BytesTransferred < operation.TotalSize)
            {
                long remaining = operation.TotalSize - operation.BytesTransferred;
                TransferredBytes += remaining;
                operation.BytesTransferred = operation.TotalSize;
            }
        }

        public void MarkOperationError(FileTransferOperation operation, string errorMessage)
        {
            operation.Status = TransferStatus.Error;
            operation.ErrorMessage = errorMessage;
            ErrorCount++;
        }

        public void MarkOperationSkipped(FileTransferOperation operation)
        {
            operation.Status = TransferStatus.Skipped;
            CompletedCount++;
        }

        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
            IsActive = false;
        }

        public void Pause()
        {
            // Implementation for pause functionality
            IsActive = false;
        }

        public void Resume()
        {
            // Implementation for resume functionality
            IsActive = true;
        }

        public bool HasErrors => ErrorCount > 0;

        public bool IsCompleted => CompletedCount + ErrorCount >= TotalCount;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}