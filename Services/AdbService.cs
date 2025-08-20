using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AdbExplorer.Models;

namespace AdbExplorer.Services
{
    public class AdbService
    {
        private string adbPath = "adb";
        private string? currentDeviceId;

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

            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error) && !error.Contains("Warning"))
            {
                throw new Exception($"ADB push failed: {error}");
            }

            // Set permissions to 660 (-rw-rw----) after pushing the file
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
}