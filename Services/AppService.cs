using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdbExplorer.Models;
using Microsoft.Win32;

namespace AdbExplorer.Services
{
    /// <summary>
    /// Provides app management for WSA (Windows Subsystem for Android) devices
    /// by reading app metadata directly from the Windows registry and filesystem.
    /// </summary>
    public class AppService
    {
        private const string WsaPackageFamilyName =
            "MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe";

        private const string UninstallRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        private static readonly string WsaLocalStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", WsaPackageFamilyName, "LocalState");

        private static readonly string WsaClientPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", WsaPackageFamilyName, "WsaClient.exe");

        /// <summary>
        /// Returns true if WSA is installed on this system.
        /// </summary>
        public static bool IsWsaInstalled()
        {
            return File.Exists(WsaClientPath) || Directory.Exists(WsaLocalStatePath);
        }

        /// <summary>
        /// Lists installed WSA apps by reading the Windows registry.
        /// Returns apps with display names, icons, and package names.
        /// </summary>
        public List<AppInfo> GetInstalledApps(bool includeSystemApps = false)
        {
            var apps = new List<AppInfo>();

            using var uninstallKey = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
            if (uninstallKey == null) return apps;

            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var appKey = uninstallKey.OpenSubKey(subKeyName);
                if (appKey == null) continue;

                string? androidPackage = appKey.GetValue("AndroidPackageName") as string;
                if (string.IsNullOrEmpty(androidPackage)) continue;

                if (!includeSystemApps && IsSystemPackage(androidPackage))
                    continue;

                string? displayName = appKey.GetValue("DisplayName") as string;
                string? displayIcon = appKey.GetValue("DisplayIcon") as string;
                string? publisher = appKey.GetValue("Publisher") as string;

                var appInfo = new AppInfo
                {
                    PackageName = androidPackage,
                    Label = !string.IsNullOrEmpty(displayName) ? displayName : DeriveLabel(androidPackage),
                    IsSystemApp = IsSystemPackage(androidPackage),
                    Icon = LoadIcon(androidPackage, displayIcon)
                };

                apps.Add(appInfo);
            }

            apps.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            return apps;
        }

        /// <summary>
        /// Launches a WSA app using WsaClient.exe.
        /// </summary>
        public void LaunchApp(string packageName)
        {
            if (File.Exists(WsaClientPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = WsaClientPath,
                    Arguments = $"/launch wsa://{packageName}",
                    UseShellExecute = false
                });
            }
            else
            {
                // Fallback: use the wsa:// protocol directly
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"wsa://{packageName}",
                    UseShellExecute = true
                });
            }
        }

        /// <summary>
        /// Opens the WSA app settings screen for the given Android package.
        /// </summary>
        public void OpenAppSettings(string packageName)
        {
            if (TryOpenWsaAppSettings(packageName))
                return;

            OpenAppSettingsViaAdb(packageName);
        }

        /// <summary>
        /// Uninstalls a WSA app via ADB and removes its registry key only after
        /// ADB reports a successful uninstall.
        /// </summary>
        public bool UninstallApp(string packageName)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = $"uninstall {packageName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process == null)
                throw new InvalidOperationException("Failed to start adb.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore cleanup failure.
                }

                throw new TimeoutException("ADB timed out while uninstalling the app.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            string details = FormatAdbProcessOutput(output, error);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(details)
                        ? $"adb uninstall exited with code {process.ExitCode}."
                        : $"adb uninstall failed:{Environment.NewLine}{details}");
            }

            if (!IsSuccessfulAdbUninstall(output, error))
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(details)
                        ? "adb uninstall did not report success."
                        : $"adb uninstall did not report success:{Environment.NewLine}{details}");
            }

            Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRegistryPath}\{packageName}", false);
            return true;
        }

        /// <summary>
        /// Gets version info for a WSA app from the registry.
        /// </summary>
        public string GetAppVersionInfo(string packageName)
        {
            using var appKey = Registry.CurrentUser.OpenSubKey(
                $@"{UninstallRegistryPath}\{packageName}");

            string info = $"Package: {packageName}";

            if (appKey != null)
            {
                string? version = appKey.GetValue("DisplayVersion") as string;
                string? publisher = appKey.GetValue("Publisher") as string;
                int? versionCode = appKey.GetValue("AndroidVersionCode") as int?;

                if (!string.IsNullOrEmpty(version))
                    info += $"\nVersion: {version}";
                if (versionCode.HasValue)
                    info += $" (code {versionCode.Value})";
                if (!string.IsNullOrEmpty(publisher))
                    info += $"\nPublisher: {publisher}";
            }

            return info;
        }

        /// <summary>
        /// Gets the path to the cached icon file (.ico) for a WSA app.
        /// </summary>
        public string? GetCachedIconPath(string packageName)
        {
            // Try .ico from WSA LocalState
            string icoPath = Path.Combine(WsaLocalStatePath, $"{packageName}.ico");
            if (File.Exists(icoPath)) return icoPath;

            string pngPath = Path.Combine(WsaLocalStatePath, $"{packageName}.png");
            if (File.Exists(pngPath)) return pngPath;

            return null;
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        public static bool IsSuccessfulAdbUninstall(string output, string error)
        {
            return GetAdbProcessMessages(output, error)
                .Any(message => string.Equals(message, "Success", StringComparison.OrdinalIgnoreCase));
        }

        public static string GetWsaAppSettingsUri(string packageName)
        {
            return $"wsa-client://app-settings?package={Uri.EscapeDataString(packageName)}";
        }

        private bool TryOpenWsaAppSettings(string packageName)
        {
            string uri = GetWsaAppSettingsUri(packageName);

            try
            {
                if (File.Exists(WsaClientPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = WsaClientPath,
                        Arguments = $"/deeplink {uri}",
                        UseShellExecute = false
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uri,
                        UseShellExecute = true
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open WSA app settings deep link: {ex.Message}");
                return false;
            }
        }

        private void OpenAppSettingsViaAdb(string packageName)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = $"shell am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:{packageName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process == null)
                throw new InvalidOperationException("Failed to start adb.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore cleanup failure.
                }

                throw new TimeoutException("ADB timed out while opening app settings.");
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                string details = FormatAdbProcessOutput(output, error);

                if (string.IsNullOrEmpty(details))
                    details = $"adb exited with code {process.ExitCode}.";

                throw new InvalidOperationException(details);
            }
        }

        private static string FormatAdbProcessOutput(string output, string error)
        {
            return string.Join(Environment.NewLine, GetAdbProcessMessages(output, error));
        }

        private static IEnumerable<string> GetAdbProcessMessages(string output, string error)
        {
            return new[] { error, output }
                .SelectMany(text => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                .Select(message => message.Trim())
                .Where(message => !string.IsNullOrEmpty(message));
        }

        private ImageSource? LoadIcon(string packageName, string? displayIconPath)
        {
            // Try the DisplayIcon path from registry first
            if (!string.IsNullOrEmpty(displayIconPath) && File.Exists(displayIconPath))
            {
                var icon = TryLoadImageFile(displayIconPath);
                if (icon != null) return icon;
            }

            // Try WSA LocalState directory
            string pngPath = Path.Combine(WsaLocalStatePath, $"{packageName}.png");
            if (File.Exists(pngPath))
            {
                var icon = TryLoadImageFile(pngPath);
                if (icon != null) return icon;
            }

            string icoPath = Path.Combine(WsaLocalStatePath, $"{packageName}.ico");
            if (File.Exists(icoPath))
            {
                var icon = TryLoadImageFile(icoPath);
                if (icon != null) return icon;
            }

            return null;
        }

        private static ImageSource? TryLoadImageFile(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 48;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public static string DeriveLabel(string packageName)
        {
            var parts = packageName.Split('.');
            string last = parts.Length > 0 ? parts[^1] : packageName;

            last = last.Replace('_', ' ').Replace('-', ' ');
            if (last.Length > 0)
            {
                last = System.Text.RegularExpressions.Regex.Replace(
                    last, @"(?<=[a-z])(?=[A-Z])", " ");
                last = string.Join(' ', last.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => char.ToUpper(w[0]) + w.Substring(1)));
            }

            return last;
        }

        // Google apps that should appear even when "Show system apps" is off
        private static readonly HashSet<string> UserFacingGoogleApps = new(StringComparer.Ordinal)
        {
            "com.android.chrome",
            "com.google.android.apps.docs",       // Google Drive
            "com.google.android.gm",              // Gmail
            "com.android.vending",                 // Play Store
        };

        private static bool IsSystemPackage(string packageName)
        {
            if (UserFacingGoogleApps.Contains(packageName))
                return false;

            return packageName.StartsWith("com.android.", StringComparison.Ordinal) ||
                   packageName.StartsWith("com.google.android.", StringComparison.Ordinal) ||
                   packageName.StartsWith("android", StringComparison.Ordinal);
        }
    }
}
