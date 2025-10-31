using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdbExplorer.Models
{
    /// <summary>
    /// Represents a single file transfer operation with progress tracking
    /// </summary>
    public class FileTransferOperation : INotifyPropertyChanged
    {
        private string _sourcePath;
        private string _destinationPath;
        private long _totalSize;
        private long _bytesTransferred;
        private TransferStatus _status;
        private string _errorMessage;
        private DateTime _startTime;
        private DateTime? _endTime;
        private double _currentSpeed;
        private bool _isDirectory;

        public string SourcePath
        {
            get => _sourcePath;
            set { _sourcePath = value; OnPropertyChanged(); }
        }

        public string DestinationPath
        {
            get => _destinationPath;
            set { _destinationPath = value; OnPropertyChanged(); }
        }

        public string FileName => System.IO.Path.GetFileName(SourcePath);

        public long TotalSize
        {
            get => _totalSize;
            set { _totalSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress)); }
        }

        public long BytesTransferred
        {
            get => _bytesTransferred;
            set
            {
                _bytesTransferred = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(FormattedProgress));
            }
        }

        public double Progress
        {
            get
            {
                if (TotalSize == 0) return 0;
                return (double)BytesTransferred / TotalSize * 100;
            }
        }

        public string FormattedProgress
        {
            get
            {
                if (Status == TransferStatus.Pending) return "Waiting...";
                if (Status == TransferStatus.Error) return "Error";
                if (Status == TransferStatus.Skipped) return "Skipped";
                if (Status == TransferStatus.Completed) return "Completed";
                if (TotalSize == 0) return "Calculating...";
                return $"{Progress:F1}%";
            }
        }

        public TransferStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedProgress));

                if (value == TransferStatus.InProgress && _startTime == default)
                {
                    _startTime = DateTime.Now;
                }
                else if (value == TransferStatus.Completed || value == TransferStatus.Error || value == TransferStatus.Skipped)
                {
                    _endTime = DateTime.Now;
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
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

        public double CurrentSpeed
        {
            get => _currentSpeed;
            set
            {
                _currentSpeed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedSpeed));
            }
        }

        public string FormattedSpeed
        {
            get
            {
                if (CurrentSpeed == 0 || Status != TransferStatus.InProgress) return "";
                return FormatSpeed(CurrentSpeed);
            }
        }

        public bool IsDirectory
        {
            get => _isDirectory;
            set { _isDirectory = value; OnPropertyChanged(); }
        }

        public TimeSpan Duration
        {
            get
            {
                if (_startTime == default) return TimeSpan.Zero;
                return (_endTime ?? DateTime.Now) - _startTime;
            }
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024)
                return $"{bytesPerSecond:F0} B/s";
            if (bytesPerSecond < 1024 * 1024)
                return $"{bytesPerSecond / 1024:F1} KB/s";
            if (bytesPerSecond < 1024 * 1024 * 1024)
                return $"{bytesPerSecond / (1024 * 1024):F1} MB/s";
            return $"{bytesPerSecond / (1024 * 1024 * 1024):F1} GB/s";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public enum TransferStatus
    {
        Pending,
        InProgress,
        Completed,
        Error,
        Skipped
    }

    public enum TransferDirection
    {
        Upload,   // Local to Device
        Download, // Device to Local
        DeviceCopy // Device to Device
    }
}