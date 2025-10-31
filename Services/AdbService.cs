using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdbExplorer.Models;

namespace AdbExplorer.Services
{
    public class AdbService
    {
        private string adbPath = "adb";
        private string? currentDeviceId;
        private CancellationTokenSource? deviceTrackerCts;
        private Task? deviceTrackerTask;
        private Process? deviceTrackerProcess;

        public AdbService()
        {
            // Try to find adb in PATH or common locations
            if (!IsAdbAvailable())
            {
                var commonPaths = new[]
                {
                    @"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe",
                    @"C:\Android\platform-tools\adb.exe",
                    @"C:\adb\adb.exe",
                    @"C:\Users\" + Environment.UserName + @"\AppData\Local\Android\Sdk\platform-tools\adb.exe"
                };

                foreach (var path in commonPaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        adbPath = path;
                        break;
                    }
                }
            }
        }

        private bool IsAdbAvailable()
        {
            try
            {
                ExecuteCommand("version");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public event EventHandler? DevicesChanged;

        public void StartDeviceTracking()
        {
            if (deviceTrackerTask != null && !deviceTrackerTask.IsCompleted)
                return;

            deviceTrackerCts?.Cancel();
            deviceTrackerCts = new CancellationTokenSource();
            Trace.WriteLine("ADB device tracker starting.");
            deviceTrackerTask = Task.Run(() => TrackDevicesLoopAsync(deviceTrackerCts.Token));
        }

        public void StopDeviceTracking()
        {
            deviceTrackerCts?.Cancel();
            deviceTrackerCts = null;

            if (deviceTrackerProcess != null)
            {
                try
                {
                    if (!deviceTrackerProcess.HasExited)
                    {
                        deviceTrackerProcess.Kill();
                    }
                }
                catch
                {
                    // Ignore cleanup failures
                }
                finally
                {
                    deviceTrackerProcess.Dispose();
                    deviceTrackerProcess = null;
                }
            }

            Trace.WriteLine("ADB device tracker stopped.");
            deviceTrackerTask = null;
        }

        private async Task TrackDevicesLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Process? process = null;
                try
                {
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = adbPath,
                            Arguments = "track-devices",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8
                        }
                    };

                    process.ErrorDataReceived += (s, args) =>
                    {
                        if (!string.IsNullOrEmpty(args.Data))
                        {
                            Trace.WriteLine($"ADB track-devices stderr: {args.Data}");
                        }
                    };

                    deviceTrackerProcess = process;
                    process.Start();
                    process.BeginErrorReadLine();

                    using var reader = process.StandardOutput;

                    Trace.WriteLine("ADB device tracker triggered initial refresh.");
                    RaiseDevicesChanged();

                    while (!reader.EndOfStream && !token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        Trace.WriteLine($"ADB track-devices output: '{line}'");

                        // Skip "List of devices" header if present
                        if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // Skip blank lines
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        // Check if line starts with 4-character hex length prefix (e.g., "0017", "0030")
                        // This indicates the start of a new device snapshot from adb track-devices
                        if (line.Length >= 4 &&
                            IsHexDigit(line[0]) && IsHexDigit(line[1]) &&
                            IsHexDigit(line[2]) && IsHexDigit(line[3]))
                        {
                            Trace.WriteLine("ADB device tracker detected new snapshot (length prefix).");
                            RaiseDevicesChanged();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"ADB device tracker error: {ex.Message}");
                }
                finally
                {
                    if (process != null)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                        }
                        catch
                        {
                        }
                        process.Dispose();
                    }

                    if (ReferenceEquals(deviceTrackerProcess, process))
                    {
                        deviceTrackerProcess = null;
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    Trace.WriteLine("ADB device tracker scheduling retry after exit.");
                    RaiseDevicesChanged();
                    try
                    {
                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }

            Trace.WriteLine("ADB device tracker loop exited.");
        }

        private void RaiseDevicesChanged()
        {
            Trace.WriteLine("ADB device tracker noticed change.");
            var handlers = DevicesChanged;
            if (handlers == null)
                return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"ADB device change handler error: {ex.Message}");
                }
            }
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        public void SetCurrentDevice(string deviceId)
        {
            currentDeviceId = deviceId;
        }

        public List<AndroidDevice> GetDevices()
        {
            var devices = new List<AndroidDevice>();
            var output = ExecuteCommand("devices -l");

            var lines = output.Split('\n').Skip(1); // Skip header
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var device = new AndroidDevice
                    {
                        Id = parts[0],
                        Status = parts[1]
                    };

                    // Extract model if available
                    var modelMatch = Regex.Match(line, @"model:(\S+)");
                    if (modelMatch.Success)
                    {
                        device.Model = modelMatch.Groups[1].Value;
                    }
                    else
                    {
                        device.Model = device.Id;
                    }

                    devices.Add(device);
                }
            }

            return devices;
        }

        public string ExecuteShellCommand(string command)
        {
            if (string.IsNullOrEmpty(currentDeviceId))
                throw new InvalidOperationException("No device selected");

            return ExecuteCommand($"-s {currentDeviceId} shell {command}");
        }

        public string ExecuteCommand(string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
            {
                // Don't throw for certain expected messages
                if (!error.Contains("adb: warning:") && !error.Contains("not found"))
                {
                    System.Diagnostics.Debug.WriteLine($"ADB Error: {error}");
                }
            }

            return output;
        }

        public bool PullFile(string remotePath, string localPath)
        {
            if (string.IsNullOrEmpty(currentDeviceId))
                throw new InvalidOperationException("No device selected");

            try
            {
                // Ensure the local directory exists
                string? localDir = System.IO.Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(localDir) && !System.IO.Directory.Exists(localDir))
                {
                    System.IO.Directory.CreateDirectory(localDir);
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = $"-s {currentDeviceId} pull \"{remotePath}\" \"{localPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                // Check if pull was successful
                if (process.ExitCode == 0 && System.IO.File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"Successfully pulled {remotePath} to {localPath}");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to pull {remotePath}: Exit code {process.ExitCode}, Error: {error}");

                    // If there's an error, throw it so we can handle it properly
                    if (!string.IsNullOrEmpty(error) && !error.Contains("Warning"))
                    {
                        throw new Exception($"ADB pull failed: {error}");
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Exception pulling file {remotePath}: {ex.Message}");
                throw;
            }
        }

        public void PushFile(string localPath, string remotePath)
        {
            PushFile(localPath, remotePath, true);
        }

        public void PushFile(string localPath, string remotePath, bool setPermissions)
        {
            if (string.IsNullOrEmpty(currentDeviceId))
                throw new InvalidOperationException("No device selected");

            if (!System.IO.File.Exists(localPath) && !System.IO.Directory.Exists(localPath))
                throw new System.IO.FileNotFoundException($"Local file not found: {localPath}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"-s {currentDeviceId} push \"{localPath}\" \"{remotePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Check if push actually failed or if it's just a non-critical error
            // "fchown failed" and "remote fchown failed" are non-critical - the file was still pushed
            bool isRealError = process.ExitCode != 0 && 
                              !string.IsNullOrEmpty(error) && 
                              !error.Contains("Warning") &&
                              !error.Contains("1 file pushed") && // If it says file pushed, it succeeded
                              !error.Contains("fchown failed"); // fchown errors are non-critical
            
            if (isRealError)
            {
                throw new Exception($"ADB push failed: {error}");
            }

            // Set permissions to 660 (-rw-rw----) after pushing the file
            if (setPermissions)
            {
                try
                {
                    ExecuteShellCommand($"chmod 660 \"{remotePath}\"");
                }
                catch
                {
                    // Ignore permission errors, some paths may not allow chmod
                }
            }
        }

        public void SetFilePermissions(string remotePath, string permissions = "660")
        {
            try
            {
                ExecuteShellCommand($"chmod {permissions} \"{remotePath}\"");
            }
            catch
            {
                // Ignore permission errors
            }
        }

        // Enhanced file transfer methods with progress tracking
        public bool PullFileWithProgress(string remotePath, string localPath, IProgress<long> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(currentDeviceId))
                throw new InvalidOperationException("No device selected");

            try
            {
                // Ensure the local directory exists
                string? localDir = System.IO.Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(localDir) && !System.IO.Directory.Exists(localDir))
                {
                    System.IO.Directory.CreateDirectory(localDir);
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = $"-s {currentDeviceId} pull \"{remotePath}\" \"{localPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };

                process.Start();

                // Start a task to monitor file size for progress
                Task progressTask = Task.Run(async () =>
                {
                    var fileInfo = new System.IO.FileInfo(localPath);
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                    {
                        if (fileInfo.Exists)
                        {
                            fileInfo.Refresh();
                            progress?.Report(fileInfo.Length);
                        }
                        await Task.Delay(100, cancellationToken);
                    }
                });

                // Read output and error streams
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                process.WaitForExit();
                progressTask.Wait(1000); // Wait for progress task to complete

                // Report final size if successful
                if (process.ExitCode == 0 && System.IO.File.Exists(localPath))
                {
                    var finalInfo = new System.IO.FileInfo(localPath);
                    progress?.Report(finalInfo.Length);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Exception pulling file with progress {remotePath}: {ex.Message}");
                throw;
            }
        }

        public bool PushFileWithProgress(string localPath, string remotePath, IProgress<long> progress, CancellationToken cancellationToken)
        {
            return PushFileWithProgress(localPath, remotePath, progress, cancellationToken, true);
        }

        public bool PushFileWithProgress(string localPath, string remotePath, IProgress<long> progress, CancellationToken cancellationToken, bool setPermissions)
        {
            if (string.IsNullOrEmpty(currentDeviceId))
                throw new InvalidOperationException("No device selected");

            if (!System.IO.File.Exists(localPath) && !System.IO.Directory.Exists(localPath))
                throw new System.IO.FileNotFoundException($"Local file not found: {localPath}");

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = $"-s {currentDeviceId} push \"{localPath}\" \"{remotePath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    }
                };

                // Get source file size
                long totalSize = 0;
                if (System.IO.File.Exists(localPath))
                {
                    var fileInfo = new System.IO.FileInfo(localPath);
                    totalSize = fileInfo.Length;
                }
                else if (System.IO.Directory.Exists(localPath))
                {
                    totalSize = GetDirectorySize(new System.IO.DirectoryInfo(localPath));
                }

                process.Start();

                // Parse progress from adb output
                Task progressTask = Task.Run(async () =>
                {
                    string line;
                    var reader = process.StandardOutput;
                    while ((line = await reader.ReadLineAsync()) != null && !cancellationToken.IsCancellationRequested)
                    {
                        // ADB push output format: "[percentage]% [transferred]/[total]"
                        // Example: "[ 45%] /data/local/tmp/file.txt"
                        if (line.Contains("%"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"\[\s*(\d+)%\]");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int percentage))
                            {
                                long bytesTransferred = (totalSize * percentage) / 100;
                                progress?.Report(bytesTransferred);
                            }
                        }
                    }
                });

                string error = process.StandardError.ReadToEnd();

                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                process.WaitForExit();
                progressTask.Wait(1000);

                // Check if push actually failed
                bool isRealError = process.ExitCode != 0 &&
                                  !string.IsNullOrEmpty(error) &&
                                  !error.Contains("Warning") &&
                                  !error.Contains("1 file pushed") &&
                                  !error.Contains("fchown failed");

                if (isRealError)
                {
                    return false;
                }

                // Report completion
                progress?.Report(totalSize);

                // Set permissions after pushing the file
                if (setPermissions)
                {
                    try
                    {
                        ExecuteShellCommand($"chmod 660 \"{remotePath}\"");
                    }
                    catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Exception pushing file with progress {localPath}: {ex.Message}");
                throw;
            }
        }

        private long GetDirectorySize(System.IO.DirectoryInfo directory)
        {
            try
            {
                long size = 0;
                var files = directory.GetFiles("*", System.IO.SearchOption.AllDirectories);
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
    }
}
