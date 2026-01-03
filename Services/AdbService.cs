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
        private readonly HashSet<string> pushMoveWorkaroundDevices = new HashSet<string>(StringComparer.Ordinal);
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

            if (RequiresPushMoveWorkaround())
            {
                if (!TryPushViaTempMove(localPath, remotePath, setPermissions, null, CancellationToken.None, 0, out string? workaroundError))
                {
                    throw new Exception($"ADB push failed (workaround): {workaroundError ?? "Unknown error"}");
                }
                return;
            }

            var result = RunAdbPush(localPath, remotePath);
            bool hasFchownError = IsFchownPermissionError(result.Error);
            if (hasFchownError)
            {
                MarkPushMoveWorkaround();
                if (!TryPushViaTempMove(localPath, remotePath, setPermissions, null, CancellationToken.None, 0, out string? workaroundError))
                {
                    throw new Exception($"ADB push failed (workaround): {workaroundError ?? "Unknown error"}");
                }
                return;
            }

            bool isRealError = IsPushError(result.ExitCode, result.Error, allowFchownWarning: true);
            if (isRealError)
            {
                throw new Exception($"ADB push failed: {result.Error}");
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

                if (RequiresPushMoveWorkaround())
                {
                    if (!TryPushViaTempMove(localPath, remotePath, setPermissions, progress, cancellationToken, totalSize, out string? workaroundError))
                    {
                        System.Diagnostics.Debug.WriteLine($"ADB push workaround failed: {workaroundError}");
                        return false;
                    }

                    progress?.Report(totalSize);
                    return true;
                }

                var result = RunAdbPushWithProgress(localPath, remotePath, progress, cancellationToken, totalSize);
                if (result.Canceled)
                {
                    return false;
                }

                bool hasFchownError = IsFchownPermissionError(result.Error);
                if (hasFchownError)
                {
                    MarkPushMoveWorkaround();
                    progress?.Report(0);
                    if (!TryPushViaTempMove(localPath, remotePath, setPermissions, progress, cancellationToken, totalSize, out string? workaroundError))
                    {
                        System.Diagnostics.Debug.WriteLine($"ADB push workaround failed: {workaroundError}");
                        return false;
                    }

                    progress?.Report(totalSize);
                    return true;
                }

                bool isRealError = IsPushError(result.ExitCode, result.Error, allowFchownWarning: true);
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

        private bool RequiresPushMoveWorkaround()
        {
            return !string.IsNullOrEmpty(currentDeviceId) && pushMoveWorkaroundDevices.Contains(currentDeviceId);
        }

        private void MarkPushMoveWorkaround()
        {
            if (!string.IsNullOrEmpty(currentDeviceId))
            {
                pushMoveWorkaroundDevices.Add(currentDeviceId);
            }
        }

        private static bool IsFchownPermissionError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return error.Contains("remote fchown failed", StringComparison.OrdinalIgnoreCase) ||
                   error.Contains("fchown failed: Operation not permitted", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPushError(int exitCode, string error, bool allowFchownWarning)
        {
            if (exitCode == 0)
                return false;

            if (string.IsNullOrEmpty(error))
                return false;

            if (error.Contains("Warning", StringComparison.OrdinalIgnoreCase))
                return false;

            if (error.Contains("1 file pushed", StringComparison.OrdinalIgnoreCase))
                return false;

            if (allowFchownWarning && error.Contains("fchown failed", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static bool HasShellCommandError(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            return output.Contains("No such", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("error", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryPushViaTempMove(string localPath, string remotePath, bool setPermissions, IProgress<long>? progress, CancellationToken cancellationToken, long totalSize, out string? errorMessage)
        {
            errorMessage = null;
            string tempPath = GetTempRemotePath();
            bool localIsDirectory = System.IO.Directory.Exists(localPath);
            string destinationPath = ResolveDestinationPath(localPath, remotePath);

            if (progress == null)
            {
                var result = RunAdbPush(localPath, tempPath);
                if (IsPushError(result.ExitCode, result.Error, allowFchownWarning: false))
                {
                    errorMessage = result.Error;
                    return false;
                }
            }
            else
            {
                var result = RunAdbPushWithProgress(localPath, tempPath, progress, cancellationToken, totalSize);
                if (result.Canceled)
                {
                    errorMessage = "Canceled";
                    return false;
                }

                if (IsPushError(result.ExitCode, result.Error, allowFchownWarning: false))
                {
                    errorMessage = result.Error;
                    return false;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                errorMessage = "Canceled";
                return false;
            }

            if (localIsDirectory && IsRemoteDirectory(destinationPath))
            {
                string copyResult = ExecuteShellCommand($"cp -r \"{tempPath}/.\" \"{destinationPath}/\" 2>&1");
                if (HasShellCommandError(copyResult))
                {
                    errorMessage = copyResult;
                    TryDeleteRemotePath(tempPath);
                    return false;
                }

                TryDeleteRemotePath(tempPath);
            }
            else
            {
                string moveResult = ExecuteShellCommand($"mv -f \"{tempPath}\" \"{destinationPath}\" 2>&1");
                if (HasShellCommandError(moveResult))
                {
                    errorMessage = moveResult;
                    TryDeleteRemotePath(tempPath);
                    return false;
                }
            }

            if (setPermissions)
            {
                try
                {
                    ExecuteShellCommand($"chmod 660 \"{destinationPath}\"");
                }
                catch
                {
                }
            }

            return true;
        }

        private void TryDeleteRemotePath(string remotePath)
        {
            try
            {
                ExecuteShellCommand($"rm -rf \"{remotePath}\"");
            }
            catch
            {
            }
        }

        private string GetTempRemotePath()
        {
            return $"/data/local/tmp/AdbExplorer_{Guid.NewGuid():N}";
        }

        private string ResolveDestinationPath(string localPath, string remotePath)
        {
            bool remoteIsDirectory = IsRemoteDirectory(remotePath) || remotePath.EndsWith("/", StringComparison.Ordinal);
            if (!remoteIsDirectory)
                return remotePath;

            string baseName = GetLocalBaseName(localPath);
            if (remotePath.EndsWith("/", StringComparison.Ordinal))
                return remotePath + baseName;

            return remotePath + "/" + baseName;
        }

        private string GetLocalBaseName(string localPath)
        {
            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(localPath);
            string name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? "payload" : name;
        }

        private bool IsRemoteDirectory(string remotePath)
        {
            try
            {
                string result = ExecuteShellCommand($"test -d \"{remotePath}\" && echo dir");
                return result.Trim().Equals("dir", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private (int ExitCode, string Error) RunAdbPush(string localPath, string remotePath)
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

            process.Start();
            _ = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, error);
        }

        private (int ExitCode, string Error, bool Canceled) RunAdbPushWithProgress(string localPath, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken, long totalSize)
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

            process.Start();

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
                        var match = Regex.Match(line, @"\[\s*(\d+)%\]");
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
                int exitCode = process.HasExited ? process.ExitCode : -1;
                return (exitCode, error, true);
            }

            process.WaitForExit();
            progressTask.Wait(1000);

            return (process.ExitCode, error, false);
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
